using System.Reflection;
using Npgsql;
using Sync.Core.Schema;

namespace Sync.Server.Migrations;

public sealed class MigrationRunner(NpgsqlDataSource dataSource, SchemaRegistry registry)
{
    private const string ResourcePrefix = "Migrations/";

    private const long LockKey = 0x5359_4E43_4D49_47;

    public sealed record Migration(int Id, string Name, string Sql);

    public static IReadOnlyList<Migration> LoadMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                var name = n[ResourcePrefix.Length..^".sql".Length];
                var id = int.Parse(name[..name.IndexOf('_')]);
                return new Migration(id, name, ReadResource(assembly, n));
            })
            .OrderBy(m => m.Id)
            .ToList();
    }

    public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, $"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                id         integer     NOT NULL PRIMARY KEY,
                name       text        NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            )
            """, cancellationToken);

        var applied = new HashSet<int>();
        await using (var command = new NpgsqlCommand("SELECT id FROM schema_migrations", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                applied.Add(reader.GetInt32(0));
        }

        var count = 0;
        foreach (var migration in LoadMigrations().Where(m => !applied.Contains(m.Id)))
        {
            await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken);
            await using var record = new NpgsqlCommand(
                "INSERT INTO schema_migrations (id, name) VALUES ($1, $2)", connection, transaction);
            record.Parameters.AddWithValue(migration.Id);
            record.Parameters.AddWithValue(migration.Name);
            await record.ExecuteNonQueryAsync(cancellationToken);
            count++;
        }

        await SeedRegistryAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    private async Task SeedRegistryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM schema_registry", cancellationToken);

        var batch = new NpgsqlBatch(connection, transaction);
        foreach (var field in registry.AllFields)
        {
            var command = new NpgsqlBatchCommand(
                "INSERT INTO schema_registry (table_name, field_name, introduced_in, category, data_type) VALUES ($1, $2, $3, $4, $5)");
            command.Parameters.Add(new NpgsqlParameter { Value = field.Table });
            command.Parameters.Add(new NpgsqlParameter { Value = field.Name });
            command.Parameters.Add(new NpgsqlParameter { Value = field.IntroducedIn });
            command.Parameters.Add(new NpgsqlParameter { Value = field.Category.ToStorageName() });
            command.Parameters.Add(new NpgsqlParameter { Value = field.Type.ToStorageName() });
            batch.BatchCommands.Add(command);
        }

        await using (batch)
            await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing migration resource '{name}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
