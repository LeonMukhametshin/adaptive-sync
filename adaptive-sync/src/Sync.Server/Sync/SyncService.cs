using System.Diagnostics;
using Npgsql;
using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Strategies;

namespace Sync.Server.Sync;

public sealed class SyncService(NpgsqlDataSource dataSource, SchemaRegistry registry, IConflictResolver resolver)
{
    private const long SyncLockKey = 0x5359_4E43_5345_53;

    public string StrategyName => resolver.Name;

    public string StrategySource => resolver.Source;

    public Task<string> DescribeStrategyAsync(CancellationToken ct) => resolver.DescribeAsync(ct);

    public async Task<SyncResponse> SyncAsync(SyncRequest request, CancellationToken ct = default, SyncTiming? timing = null)
    {
        var started = Stopwatch.GetTimestamp();
        Validate(request);
        var context = new SyncContext(registry, request.ClientId, request.SchemaVersion);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var lockCommand = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({SyncLockKey})", connection, transaction))
            await lockCommand.ExecuteNonQueryAsync(ct);

        var store = new RecordStore(connection, transaction, registry);
        var response = new SyncResponse();
        var ownWrites = new Dictionary<(string Table, Guid Id), OwnWrites>();
        var affected = new List<(string Table, Guid Id)>();

        foreach (var operation in request.Operations)
        {
            var key = (operation.Table, operation.RecordId);
            var logged = await store.FindLoggedResultAsync(operation.OpId, ct);
            var result = logged ?? await ProcessAsync(request, context, operation, store, transaction, Own(ownWrites, key), timing, ct);
            if (result.Outcome != OperationOutcome.Rejected)
                Own(ownWrites, key).Add(result.RowVersion);
            response.Results.Add(result);

            if (registry.TablesFor(request.SchemaVersion).Any(t => t.Name == operation.Table) && !affected.Contains(key))
                affected.Add(key);
        }

        foreach (var (table, id) in affected)
            response.Records.Add(await store.LoadStateAsync(registry.GetTable(table), id, request.SchemaVersion, ct));

        var affectedSet = affected.ToHashSet();
        response.RemoteChanges = (await store.LoadChangesSinceAsync(request.SchemaVersion, request.SinceServerVersion, ct))
            .Where(s => !affectedSet.Contains((s.Table, s.RecordId)))
            .ToList();
        response.ServerVersion = await store.CurrentServerVersionAsync(ct);

        await transaction.CommitAsync(ct);
        timing?.Complete(Stopwatch.GetTimestamp() - started);
        return response;
    }

    private async Task<OperationResult> ProcessAsync(
        SyncRequest request, SyncContext context, SyncOperation operation, RecordStore store, NpgsqlTransaction transaction,
        OwnWrites own, SyncTiming? timing, CancellationToken ct)
    {
        var error = ValidateOperation(context, operation);
        if (error != null)
        {
            var rejected = new OperationResult { OpId = operation.OpId, Outcome = OperationOutcome.Rejected, Message = "Protocol error: " + error };
            await store.LogAsync(request, operation, resolver.Name, rejected, Array.Empty<FieldDecision>(), ct);
            return rejected;
        }

        var table = registry.GetTable(operation.Table);
        var normalized = Normalize(table, operation);

        await transaction.SaveAsync("op", ct);
        try
        {
            var current = await store.LoadForUpdateAsync(table, operation.RecordId, ct);
            var snapshot = current == null ? null : own.Mask(current, normalized.BaseRowVersion);

            var decideStarted = Stopwatch.GetTimestamp();
            var decision = await resolver.DecideAsync(context, normalized, snapshot, ct);
            timing?.AddDecision(Stopwatch.GetTimestamp() - decideStarted);
            var result = await ApplyAsync(table, normalized, current, decision, store, ct);

            await store.LogAsync(request, operation, resolver.Name, result, decision.Fields, ct);
            await transaction.ReleaseAsync("op", ct);
            return result;
        }
        catch (PostgresException ex) when (ex.SqlState.StartsWith("23", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync("op", ct);
            var rejected = new OperationResult
            {
                OpId = operation.OpId,
                Outcome = OperationOutcome.Rejected,
                Message = "Constraint violation: " + ex.MessageText,
            };
            await store.LogAsync(request, operation, resolver.Name, rejected, Array.Empty<FieldDecision>(), ct);
            return rejected;
        }
    }

    private static OwnWrites Own(Dictionary<(string Table, Guid Id), OwnWrites> ownWrites, (string Table, Guid Id) key)
    {
        if (!ownWrites.TryGetValue(key, out var own))
            ownWrites[key] = own = new OwnWrites();
        return own;
    }

    private static async Task<OperationResult> ApplyAsync(
        TableDefinition table, SyncOperation operation, ServerRecord? current, StrategyDecision decision, RecordStore store,
        CancellationToken ct)
    {
        var result = new OperationResult
        {
            OpId = operation.OpId,
            Outcome = decision.Outcome,
            Message = decision.Message,
            Situation = decision.Situation,
            Fields = decision.Fields
                .Select(f => new FieldResult { Field = f.Field, Outcome = f.Outcome, Situation = f.Situation })
                .ToList(),
        };
        if (decision.Outcome == OperationOutcome.Rejected)
        {
            result.RowVersion = current?.RowVersion ?? 0;
            return result;
        }

        foreach (var field in decision.FieldWrites.Keys)
        {
            if (table.FindField(field) == null)
                throw new InvalidOperationException($"Strategy wrote unknown field '{table.Name}.{field}'.");
        }

        var rowVersion = await store.NextRowVersionAsync(ct);
        var deletedAt = decision.HardDelete || decision.Tombstone ? operation.LogicalTime : (long?)null;

        if (current == null)
        {
            if (deletedAt.HasValue)
                throw new InvalidOperationException("Strategy deleted a record that does not exist.");
            await store.InsertAsync(table, operation.RecordId, decision.FieldWrites, rowVersion, operation.LogicalTime, ct);
        }
        else
        {
            await store.UpdateAsync(table, operation.RecordId, decision.FieldWrites, rowVersion, operation.LogicalTime, deletedAt, ct);
        }

        result.RowVersion = rowVersion;
        return result;
    }

    private void Validate(SyncRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
            throw new SyncProtocolException("client_id is required.");
        if (!registry.IsSupported(request.SchemaVersion))
            throw new SyncProtocolException($"Schema version {request.SchemaVersion} is not supported.");
        if (request.SinceServerVersion < 0)
            throw new SyncProtocolException("since_server_version cannot be negative.");
        if (request.Operations.Select(o => o.OpId).Distinct().Count() != request.Operations.Count)
            throw new SyncProtocolException("Operation ids must be unique within a request.");
    }

    private string? ValidateOperation(SyncContext context, SyncOperation operation)
    {
        if (operation.OpId == Guid.Empty) return "op_id is required.";
        if (operation.RecordId == Guid.Empty) return "record_id is required.";
        if (!registry.TablesFor(context.ClientSchemaVersion).Any(t => t.Name == operation.Table))
            return $"table '{operation.Table}' is not part of schema v{context.ClientSchemaVersion}.";
        if (operation.Type == OperationType.Delete && operation.Changes.Count > 0)
            return "delete must not carry field changes.";
        if (operation.Changes.Select(c => c.Field).Distinct().Count() != operation.Changes.Count)
            return "a field is changed twice in one operation.";

        var table = registry.GetTable(operation.Table);
        foreach (var change in operation.Changes)
        {
            if (!context.IsKnown(operation.Table, change.Field))
                return $"field '{operation.Table}.{change.Field}' is outside K(v{context.ClientSchemaVersion}).";
            var field = table.FindField(change.Field)!;
            try
            {
                FieldValues.Normalize(field.Type, change.OldValue);
                FieldValues.Normalize(field.Type, change.NewValue);
            }
            catch (FormatException ex)
            {
                return $"field '{operation.Table}.{change.Field}': {ex.Message}";
            }
        }
        return null;
    }

    private static SyncOperation Normalize(TableDefinition table, SyncOperation operation) =>
        new()
        {
            OpId = operation.OpId,
            Table = operation.Table,
            RecordId = operation.RecordId,
            Type = operation.Type,
            BaseRowVersion = operation.BaseRowVersion,
            LogicalTime = operation.LogicalTime,
            Changes = operation.Changes
                .Select(c =>
                {
                    var type = table.FindField(c.Field)!.Type;
                    return new FieldChange
                    {
                        Field = c.Field,
                        OldValue = FieldValues.Normalize(type, c.OldValue),
                        NewValue = FieldValues.Normalize(type, c.NewValue),
                    };
                })
                .ToList(),
        };
}

public sealed class SyncTiming
{
    public int Decisions { get; private set; }

    public long DecideTicks { get; private set; }

    public long TotalTicks { get; private set; }

    public void AddDecision(long ticks)
    {
        Decisions++;
        DecideTicks += ticks;
    }

    public void Complete(long ticks) => TotalTicks = ticks;

    public string ToServerTimingHeader() =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "sync;dur={0:0.###}, decide;dur={1:0.###};desc=\"{2}\"",
            Milliseconds(TotalTicks), Milliseconds(DecideTicks), Decisions);

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
