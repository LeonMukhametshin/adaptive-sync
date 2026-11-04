using Npgsql;
using Testcontainers.PostgreSql;

namespace Sync.Integration.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            UnavailableReason = "Docker is not available: " + ex.Message;
            _container = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
            await _container.DisposeAsync();
    }

    public async Task<NpgsqlDataSource> CreateDatabaseAsync() =>
        NpgsqlDataSource.Create(await CreateDatabaseConnectionStringAsync());

    public async Task<string> CreateDatabaseConnectionStringAsync()
    {
        Skip.If(_container == null, UnavailableReason);

        var name = "test_" + Guid.NewGuid().ToString("N");
        await using (var admin = NpgsqlDataSource.Create(_container!.GetConnectionString()))
        await using (var command = admin.CreateCommand($"CREATE DATABASE {name}"))
            await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
