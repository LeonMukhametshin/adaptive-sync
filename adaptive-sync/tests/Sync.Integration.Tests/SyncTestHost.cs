using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sync.Client;
using Sync.Client.Transport;
using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Time;
using Sync.Server.Sync;

namespace Sync.Integration.Tests;

public sealed class SyncTestHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _directory;
    private readonly List<SyncClient> _clients = new();

    private SyncTestHost(
        string connectionString, string strategy, string? resolverUrl, Func<HttpMessageHandler>? resolverHandler,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        ConnectionString = connectionString;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Sync", connectionString);
            builder.UseSetting("Sync:Strategy", strategy);
            if (resolverUrl != null)
                builder.UseSetting("Sync:ResolverUrl", resolverUrl);
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
                builder.UseSetting(key, value);
            if (resolverHandler != null)
            {
                builder.ConfigureTestServices(services => services
                    .AddHttpClient(RemoteConflictResolver.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(resolverHandler));
            }
        });
        _directory = Path.Combine(Path.GetTempPath(), "adaptive-sync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public string ConnectionString { get; }

    public LogicalClock Clock { get; } = new();

    public static async Task<SyncTestHost> StartAsync(
        PostgresFixture postgres, string strategy = "ServerWins", IReadOnlyDictionary<string, string>? settings = null)
    {
        var host = new SyncTestHost(await postgres.CreateDatabaseConnectionStringAsync(), strategy, null, null, settings);
        host._factory.CreateClient().Dispose();
        return host;
    }

    public static async Task<SyncTestHost> StartWithResolverAsync(
        PostgresFixture postgres, string resolverUrl, Func<HttpMessageHandler>? resolverHandler = null,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        var host = new SyncTestHost(await postgres.CreateDatabaseConnectionStringAsync(), "ServerWins", resolverUrl, resolverHandler, settings);
        host._factory.CreateClient().Dispose();
        return host;
    }

    public HttpClient CreateHttpClient() => _factory.CreateClient();

    public HttpSyncTransport CreateTransport() => new(_factory.CreateClient());

    public SyncClient CreateClient(string clientId, int schemaVersion, int seed, ISyncTransport? transport = null)
    {
        var client = SyncClient.Open(
            Path.Combine(_directory, clientId + ".db"), clientId, schemaVersion, transport ?? CreateTransport(), Clock,
            DeterministicGuids.FromSeed(seed));
        _clients.Add(client);
        return client;
    }

    public async Task<List<RecordState>> ServerStateAsync(string table, int clientVersion)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var store = new RecordStore(connection, transaction, SchemaVersions.Registry);
        var states = await store.LoadChangesSinceAsync(clientVersion, 0, CancellationToken.None);
        return states.Where(s => s.Table == table).OrderBy(s => s.RecordId).ToList();
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] parameters)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            client.Dispose();
        await _factory.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class RecordingTransport(ISyncTransport inner) : ISyncTransport
{
    public List<SyncRequest> Requests { get; } = new();

    public Task<SyncResponse> SendAsync(SyncRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return inner.SendAsync(request, cancellationToken);
    }
}

public sealed class LostResponseTransport(ISyncTransport inner) : ISyncTransport
{
    public int FailuresLeft { get; set; } = 1;

    public async Task<SyncResponse> SendAsync(SyncRequest request, CancellationToken cancellationToken = default)
    {
        var response = await inner.SendAsync(request, cancellationToken);
        if (FailuresLeft <= 0) return response;
        FailuresLeft--;
        throw new HttpRequestException("Response lost after the server processed the request.");
    }
}
