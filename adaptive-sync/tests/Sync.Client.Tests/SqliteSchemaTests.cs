using Microsoft.Data.Sqlite;
using Sync.Client.Storage;
using Sync.Core.Schema;

namespace Sync.Client.Tests;

public class SqliteSchemaTests
{
    private static readonly SchemaRegistry Registry = SchemaVersions.Registry;

    private static readonly string[] ServiceColumns =
        { SchemaRegistry.IdColumn, SchemaRegistry.RowVersionColumn, SchemaRegistry.IsDeletedColumn };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EnsureCreated_CreatesExactlyKnownFields(int version)
    {
        using var connection = OpenInMemory();

        SqliteSchema.EnsureCreated(connection, Registry, version);

        var userColumns = new HashSet<FieldRef>();
        foreach (var table in UserTables(connection))
        {
            var columns = Columns(connection, table);
            Assert.Subset(columns.ToHashSet(), ServiceColumns.ToHashSet());
            foreach (var column in columns.Except(ServiceColumns))
                userColumns.Add(new FieldRef(table, column));
        }

        Assert.Equal(Registry.KnownFields(version).ToHashSet(), userColumns);
        Assert.Equal(version, SqliteSchema.ReadVersion(connection));
    }

    [Theory]
    [InlineData(1, new[] { "inventory_item", "player_profile" })]
    [InlineData(2, new[] { "inventory_item", "player_profile" })]
    [InlineData(3, new[] { "inventory_item", "item_modification", "player_profile" })]
    public void EnsureCreated_CreatesTablesOfVersion(int version, string[] expected)
    {
        using var connection = OpenInMemory();

        SqliteSchema.EnsureCreated(connection, Registry, version);

        Assert.Equal(expected, UserTables(connection).OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void EnsureCreated_AppliesDefaults()
    {
        using var connection = OpenInMemory();
        SqliteSchema.EnsureCreated(connection, Registry, SchemaVersions.V2);

        Execute(connection, "INSERT INTO player_profile (id, nickname) VALUES ('p1', 'neo')");

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT level, coins, settings_sound, premium_currency, row_version, is_deleted FROM player_profile";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(new long[] { 1, 0, 1, 0, 0, 0 }, Enumerable.Range(0, 6).Select(reader.GetInt64));
    }

    [Fact]
    public void EnsureCreated_V3_EnforcesItemModificationForeignKey()
    {
        using var connection = OpenInMemory();
        SqliteSchema.EnsureCreated(connection, Registry, SchemaVersions.V3);

        var ex = Assert.Throws<SqliteException>(() =>
            Execute(connection, "INSERT INTO item_modification (id, item_id) VALUES ('m1', 'missing-item')"));
        Assert.Contains("FOREIGN KEY", ex.Message);

        Execute(connection, "INSERT INTO player_profile (id) VALUES ('p1')");
        Execute(connection, "INSERT INTO inventory_item (id, player_id, item_type) VALUES ('i1', 'p1', 'sword')");
        Execute(connection, "INSERT INTO item_modification (id, item_id, mod_type) VALUES ('m1', 'i1', 'sharpness')");
    }

    [Fact]
    public void EnsureCreated_IsIdempotentForSameVersion()
    {
        using var connection = OpenInMemory();

        SqliteSchema.EnsureCreated(connection, Registry, SchemaVersions.V1);
        SqliteSchema.EnsureCreated(connection, Registry, SchemaVersions.V1);

        Assert.Equal(SchemaVersions.V1, SqliteSchema.ReadVersion(connection));
    }

    [Fact]
    public void EnsureCreated_RejectsDifferentExistingVersion()
    {
        using var connection = OpenInMemory();
        SqliteSchema.EnsureCreated(connection, Registry, SchemaVersions.V1);

        Assert.Throws<NotSupportedException>(() => SqliteSchema.EnsureCreated(connection, Registry, SchemaVersions.V3));
    }

    [Fact]
    public void ReadVersion_IsNullForEmptyDatabase()
    {
        using var connection = OpenInMemory();

        Assert.Null(SqliteSchema.ReadVersion(connection));
    }

    private static SqliteConnection OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        return connection;
    }

    private static List<string> UserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read()) tables.Add(reader.GetString(0));
        return tables.Except(SqliteSchema.ServiceTables).ToList();
    }

    private static List<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
