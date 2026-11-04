using System.Net.Http.Json;
using Sync.Core.Protocol;
using Sync.Core.Resolver;
using Sync.Core.Schema;
using Sync.Core.Strategies;

namespace Sync.Server.Sync;

public interface IConflictResolver
{
    string Name { get; }

    string Source { get; }

    Task<StrategyDecision> DecideAsync(SyncContext context, SyncOperation operation, ServerRecord? current, CancellationToken ct);

    Task<string> DescribeAsync(CancellationToken ct);

    Task<string> SwitchAsync(string strategy, CancellationToken ct);
}

public sealed class StrategySwitchException(string message) : Exception(message);

public sealed class LocalConflictResolver(ISyncStrategy initial) : IConflictResolver
{
    public const string SourceName = "builtin";

    private ISyncStrategy _strategy = initial;

    public string Name => Strategy.Name;

    public string Source => SourceName;

    private ISyncStrategy Strategy => Volatile.Read(ref _strategy);

    public Task<StrategyDecision> DecideAsync(SyncContext context, SyncOperation operation, ServerRecord? current, CancellationToken ct) =>
        Task.FromResult(Strategy.Decide(context, operation, current));

    public Task<string> DescribeAsync(CancellationToken ct) => Task.FromResult(Strategy.Name);

    public Task<string> SwitchAsync(string strategy, CancellationToken ct)
    {
        Volatile.Write(ref _strategy, global::Sync.Strategies.SyncStrategies.Create(strategy));
        return Task.FromResult(Strategy.Name);
    }
}

public sealed class ResolverUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class RemoteConflictResolver(HttpClient http, SchemaRegistry registry) : IConflictResolver
{
    public const string SourceName = "resolver";
    public const string HttpClientName = "resolver";
    public const string UnknownName = "Remote";

    private string _name = UnknownName;

    public string Name => _name;

    public string Source => SourceName;

    public async Task<StrategyDecision> DecideAsync(SyncContext context, SyncOperation operation, ServerRecord? current, CancellationToken ct)
    {
        var request = new ResolveRequest
        {
            ClientId = context.ClientId,
            SchemaVersion = context.ClientSchemaVersion,
            Items = { new ResolveItem { Operation = operation, Current = current } },
        };

        ResolveResponse? response;
        try
        {
            using var message = await http.PostAsJsonAsync(ResolveContract.ResolvePath, request, SyncJson.Options, ct);
            if (!message.IsSuccessStatusCode)
                throw new ResolverUnavailableException(
                    $"Resolver returned {(int)message.StatusCode}: {await message.Content.ReadAsStringAsync(ct)}");
            response = await message.Content.ReadFromJsonAsync<ResolveResponse>(SyncJson.Options, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ResolverUnavailableException("Resolver is unavailable at " + http.BaseAddress + ": " + ex.Message, ex);
        }

        if (response == null || response.Decisions.Count != 1)
            throw new ResolverUnavailableException("Resolver returned a malformed response.");

        _name = response.Strategy;
        var decision = response.Decisions[0];
        ResolveValues.Normalize(registry, operation.Table, decision);
        return decision;
    }

    public async Task<string> SwitchAsync(string strategy, CancellationToken ct)
    {
        HttpResponseMessage message;
        try
        {
            message = await http.PostAsJsonAsync(ResolveContract.StrategyPath, new StrategySwitchRequest { Strategy = strategy }, SyncJson.Options, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ResolverUnavailableException("Resolver is unavailable at " + http.BaseAddress + ": " + ex.Message, ex);
        }

        using (message)
        {
            var text = await message.Content.ReadAsStringAsync(ct);
            if ((int)message.StatusCode == StatusCodes.Status400BadRequest)
                throw new ArgumentException(text);
            if (!message.IsSuccessStatusCode)
                throw new StrategySwitchException($"Resolver refused the switch ({(int)message.StatusCode}): {text}");
            var health = System.Text.Json.JsonSerializer.Deserialize<ResolverHealth>(text, SyncJson.Options);
            _name = health?.Strategy ?? UnknownName;
            return _name;
        }
    }

    public async Task<string> DescribeAsync(CancellationToken ct)
    {
        try
        {
            var health = await http.GetFromJsonAsync<ResolverHealth>(ResolveContract.HealthPath, SyncJson.Options, ct);
            _name = health?.Strategy ?? UnknownName;
            return _name;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return "unavailable";
        }
    }
}
