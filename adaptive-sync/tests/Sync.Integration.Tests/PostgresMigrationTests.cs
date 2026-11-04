using Npgsql;
using Sync.Core.Schema;
using Sync.Server.Migrations;

namespace Sync.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class PostgresMigrationTests(PostgresFixture postgres)
{
    private static readonly SchemaRegistry Registry = SchemaVersions.Registry;

    private static readonly Dictionary<string, string> ServiceColumns = new()
    {
        [SchemaRegistry.IdColumn] = "uuid",
        [SchemaRegistry.RowVersionColumn] = "bigint",
        [SchemaRegistry.FieldVersionsColumn] = "jsonb",
        [SchemaRegistry.IsDeletedColumn] = "boolean",
        [SchemaRegistry.DeletedAtColumn] = "bigint",
    };

    [Fact]
    public void Migrations_AreNumberedContiguously()
    {
        var migrations = MigrationRunner.LoadMigrations();

        Assert.Equal(Enumerable.Range(1, migrations.Count), migrations.Select(m => m.Id));
        Assert.True(migrations.Count >= Registry.LatestVersion);
    }

    [SkippableFact]
    public async Task Migrate_AppliesAllMigrationsOnce()
    {
        await using var dataSource = await postgres.CreateDatabaseAsync();
        var runner = new MigrationRunner(dataSource, Registry);

        Assert.Equal(MigrationRunner.LoadMigrations().Count, await runner.MigrateAsync());
        Assert.Equal(0, await runner.MigrateAsync());
    }

    [SkippableFact]
    public async Task Migrate_CreatesV3TablesWithRegistryFieldsAndServiceColumns()
    {
        await using var dataSource = await postgres.CreateDatabaseAsync();
        await new MigrationRunner(dataSource, Registry).MigrateAsync();

        var columns = await ReadColumnsAsync(dataSource);

        foreach (var table in Registry.Tables)
        {
            var expected = table.Fields.ToDictionary(
                f => f.Name,
                f => (Type: PgType(f.Type), Nullable: f.IsNullable));
            foreach (var (name, type) in ServiceColumns)
                expected[name] = (type, name == SchemaRegistry.DeletedAtColumn);

            Assert.True(columns.TryGetValue(table.Name, out var actual), $"table {table.Name} is missing");
            Assert.Equal(expected.OrderBy(p => p.Key), actual!.OrderBy(p => p.Key));
        }

        Assert.Equal(
            Registry.Tables.Select(t => t.Name).Append("schema_migrations").Append("schema_registry").Append("sync_operation_log").OrderBy(n => n),
            columns.Keys.OrderBy(n => n));
    }

    [SkippableFact]
    public async Task Migrate_SeedsSchemaRegistryFromCode()
    {
        await using var dataSource = await postgres.CreateDatabaseAsync();
        await new MigrationRunner(dataSource, Registry).MigrateAsync();

        var rows = new List<string>();
        await using (var command = dataSource.CreateCommand(
            "SELECT table_name, field_name, introduced_in, category, data_type FROM schema_registry"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                rows.Add($"{reader.GetString(0)}.{reader.GetString(1)} v{reader.GetInt32(2)} {reader.GetString(3)} {reader.GetString(4)}");
        }

        var expected = Registry.AllFields
            .Select(f => $"{f.Table}.{f.Name} v{f.IntroducedIn} {f.Category.ToStorageName()} {f.Type.ToStorageName()}");
        Assert.Equal(expected.OrderBy(s => s, StringComparer.Ordinal), rows.OrderBy(s => s, StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task Migrate_EnforcesItemModificationForeignKey()
    {
        await using var dataSource = await postgres.CreateDatabaseAsync();
        await new MigrationRunner(dataSource, Registry).MigrateAsync();

        await using var orphan = dataSource.CreateCommand(
            "INSERT INTO item_modification (id, item_id) VALUES (gen_random_uuid(), gen_random_uuid())");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => orphan.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    private static string PgType(FieldType type) => type switch
    {
        FieldType.Text => "text",
        FieldType.Integer => "bigint",
        FieldType.Boolean => "boolean",
        FieldType.Uuid => "uuid",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static async Task<Dictionary<string, Dictionary<string, (string Type, bool Nullable)>>> ReadColumnsAsync(
        NpgsqlDataSource dataSource)
    {
        var result = new Dictionary<string, Dictionary<string, (string, bool)>>();
        await using var command = dataSource.CreateCommand(
            "SELECT table_name, column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'public'");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            if (!result.TryGetValue(table, out var columns))
                result[table] = columns = new Dictionary<string, (string, bool)>();
            columns[reader.GetString(1)] = (reader.GetString(2), reader.GetString(3) == "YES");
        }
        return result;
    }
}
