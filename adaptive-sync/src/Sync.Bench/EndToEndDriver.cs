using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Oracle;
using Sync.Server.Sync;
using Sync.Simulation;

namespace Sync.Bench;

public sealed record RequestSample(
    string Kind, int Operations, long RequestBytes, long ResponseBytes, double WallMs, double ServerMs, double DecideMs, int Decisions);

public sealed record EndToEndRun(IReadOnlyList<RequestSample> Requests, IReadOnlyList<string> Mismatches);

public sealed class EndToEndDriver(HttpClient http, string connectionString, SchemaRegistry registry)
{
    public const string Pull = "pull";
    public const string Push = "push";

    private static readonly Regex TimingEntry = new(@"(?<name>\w+);dur=(?<dur>[0-9.]+)(;desc=""(?<desc>[^""]*)"")?", RegexOptions.Compiled);

    public async Task<EndToEndRun> RunAsync(OracleInput input, ReplayResult replay, CancellationToken ct = default)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await ResetAsync(dataSource, ct);
        await SeedAsync(dataSource, input, ct);

        var sessions = replay.Steps
            .GroupBy(s => (s.Event.Author, s.Event.SeenAt, s.Event.DeliveredAt))
            .Select(g => new Session(g.Key.Author, g.First().Event.SchemaVersion, g.Key.SeenAt, g.ToList()))
            .ToList();
        var firstStep = sessions.ToDictionary(s => s.Steps[0].EventIndex);
        var pulls = new Queue<Session>(sessions.OrderBy(s => s.SeenAt).ThenBy(s => s.Steps[0].EventIndex));
        var known = new Dictionary<string, long>();
        var samples = new List<RequestSample>();
        var mismatches = new List<string>();

        for (var index = 0; index < input.Events.Count; index++)
        {
            var e = input.Events[index];
            while (pulls.Count > 0 && pulls.Peek().SeenAt < e.DeliveredAt)
                await PullAsync(pulls.Dequeue(), known, samples, ct);

            if (e.IsServer)
                await ServerEventAsync(dataSource, e, ct);
            else if (firstStep.TryGetValue(index, out var session))
                await PushAsync(session, known, samples, mismatches, ct);
        }
        while (pulls.Count > 0)
            await PullAsync(pulls.Dequeue(), known, samples, ct);

        mismatches.AddRange(await CompareAsync(dataSource, replay, ct));
        return new EndToEndRun(samples, mismatches);
    }

    private async Task PullAsync(Session session, Dictionary<string, long> known, List<RequestSample> samples, CancellationToken ct)
    {
        var request = new SyncRequest
        {
            ClientId = session.Author,
            AppVersion = "bench",
            SchemaVersion = session.SchemaVersion,
            SinceServerVersion = known.TryGetValue(session.Author, out var since) ? since : 0,
        };
        var (response, sample) = await SendAsync(Pull, request, ct);
        session.PulledVersion = response.ServerVersion;
        known[session.Author] = response.ServerVersion;
        samples.Add(sample);
    }

    private async Task PushAsync(
        Session session, Dictionary<string, long> known, List<RequestSample> samples, List<string> mismatches, CancellationToken ct)
    {
        var request = new SyncRequest
        {
            ClientId = session.Author,
            AppVersion = "bench",
            SchemaVersion = session.SchemaVersion,
            SinceServerVersion = session.PulledVersion ?? (known.TryGetValue(session.Author, out var since) ? since : 0),
            Operations = session.Steps.Select(s => s.Operation).ToList(),
        };
        var (response, sample) = await SendAsync(Push, request, ct);
        known[session.Author] = response.ServerVersion;
        samples.Add(sample);

        for (var i = 0; i < session.Steps.Count; i++)
        {
            var expected = session.Steps[i].Decision.Outcome;
            var actual = response.Results[i].Outcome;
            if (expected != actual)
                mismatches.Add($"event {session.Steps[i].EventIndex}: outcome {actual}, model {expected}");
        }
    }

    private async Task<(SyncResponse Response, RequestSample Sample)> SendAsync(string kind, SyncRequest request, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(request, SyncJson.Options);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var started = Stopwatch.GetTimestamp();
        using var message = await http.PostAsync("sync", content, ct);
        var text = await message.Content.ReadAsStringAsync(ct);
        var wall = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        if (!message.IsSuccessStatusCode)
            throw new HttpRequestException($"Sync failed with {(int)message.StatusCode}: {text}");

        double serverMs = 0, decideMs = 0;
        var decisions = 0;
        if (message.Headers.TryGetValues("Server-Timing", out var values))
        {
            foreach (Match match in TimingEntry.Matches(string.Join(",", values)))
            {
                var duration = double.Parse(match.Groups["dur"].Value, CultureInfo.InvariantCulture);
                if (match.Groups["name"].Value == "sync") serverMs = duration;
                if (match.Groups["name"].Value != "decide") continue;
                decideMs = duration;
                decisions = int.Parse(match.Groups["desc"].Value, CultureInfo.InvariantCulture);
            }
        }

        var response = JsonSerializer.Deserialize<SyncResponse>(text, SyncJson.Options)
                       ?? throw new InvalidOperationException("Empty sync response.");
        return (response, new RequestSample(kind, request.Operations.Count, Encoding.UTF8.GetByteCount(body),
            Encoding.UTF8.GetByteCount(text), wall, serverMs, decideMs, decisions));
    }

    private async Task ResetAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var tables = string.Join(", ", registry.Tables.Select(t => t.Name).Append("sync_operation_log"));
        await using var command = dataSource.CreateCommand(
            $"TRUNCATE {tables} CASCADE; ALTER SEQUENCE sync_row_version_seq RESTART WITH 1;");
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task SeedAsync(NpgsqlDataSource dataSource, OracleInput input, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var store = new RecordStore(connection, transaction, registry);
        foreach (var record in input.InitialRecords)
        {
            var table = registry.GetTable(record.Table);
            var writes = table.Fields.ToDictionary(
                f => f.Name,
                f => record.Values.TryGetValue(f.Name, out var value) ? FieldValues.Normalize(f.Type, value) : f.DefaultValue);
            await store.InsertAsync(table, record.RecordId, writes, await store.NextRowVersionAsync(ct), 0, ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async Task ServerEventAsync(NpgsqlDataSource dataSource, OracleEvent e, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var store = new RecordStore(connection, transaction, registry);
        var table = registry.GetTable(e.Table);
        var current = await store.LoadForUpdateAsync(table, e.RecordId, ct);
        var writes = e.Changes.ToDictionary(c => c.Field, c => FieldValues.Normalize(table.FindField(c.Field)!.Type, c.NewValue));
        if (current == null && e.Operation == OracleOperation.Insert)
            await store.InsertAsync(table, e.RecordId, writes, await store.NextRowVersionAsync(ct), e.MadeAt, ct);
        else if (current is { IsDeleted: false })
            await store.UpdateAsync(table, e.RecordId, writes, await store.NextRowVersionAsync(ct), e.MadeAt,
                e.Operation == OracleOperation.Delete ? e.MadeAt : null, ct);
        else
            return;
        await transaction.CommitAsync(ct);
    }

    private async Task<List<string>> CompareAsync(NpgsqlDataSource dataSource, ReplayResult replay, CancellationToken ct)
    {
        var mismatches = new List<string>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var store = new RecordStore(connection, transaction, registry);
        foreach (var ((tableName, id), expected) in replay.Records)
        {
            var table = registry.GetTable(tableName);
            var actual = await store.LoadStateAsync(table, id, registry.LatestVersion, ct);
            if (actual.IsDeleted != expected.IsDeleted)
                mismatches.Add($"{tableName}/{id}: deleted {actual.IsDeleted}, model {expected.IsDeleted}");
            if (actual.RowVersion != expected.RowVersion)
                mismatches.Add($"{tableName}/{id}: row_version {actual.RowVersion}, model {expected.RowVersion}");
            foreach (var field in table.Fields)
            {
                var value = actual.Values.TryGetValue(field.Name, out var v) ? v : null;
                if (!FieldValues.AreEqual(field.Type, value, expected.Values[field.Name]))
                    mismatches.Add($"{tableName}/{id}.{field.Name}: {value}, model {expected.Values[field.Name]}");
            }
        }
        return mismatches;
    }

    private sealed class Session(string author, int schemaVersion, long seenAt, List<ReplayStep> steps)
    {
        public string Author { get; } = author;

        public int SchemaVersion { get; } = schemaVersion;

        public long SeenAt { get; } = seenAt;

        public List<ReplayStep> Steps { get; } = steps;

        public long? PulledVersion { get; set; }
    }
}
