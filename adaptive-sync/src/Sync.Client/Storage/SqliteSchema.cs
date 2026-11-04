using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Sync.Core.Schema;

namespace Sync.Client.Storage
{
    public static class SqliteSchema
    {
        public const string SchemaInfoTable = "schema_info";
        public const string JournalTable = "op_journal";
        public const string SyncStateTable = "sync_state";

        public static IReadOnlyList<string> ServiceTables { get; } = new[] { SchemaInfoTable, JournalTable, SyncStateTable };

        public static string BuildCreateScript(SchemaRegistry registry, int version)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            var sb = new StringBuilder();
            sb.Append("CREATE TABLE ").Append(SchemaInfoTable).Append(" (version INTEGER NOT NULL);\n");
            sb.Append("CREATE TABLE ").Append(JournalTable).Append(" (\n")
              .Append("    seq INTEGER PRIMARY KEY AUTOINCREMENT,\n")
              .Append("    op_id TEXT NOT NULL UNIQUE,\n")
              .Append("    table_name TEXT NOT NULL,\n")
              .Append("    record_id TEXT NOT NULL,\n")
              .Append("    op_type TEXT NOT NULL,\n")
              .Append("    base_row_version INTEGER NOT NULL,\n")
              .Append("    changes TEXT NOT NULL,\n")
              .Append("    logical_time INTEGER NOT NULL\n")
              .Append(");\n");
            sb.Append("CREATE TABLE ").Append(SyncStateTable).Append(" (server_version INTEGER NOT NULL);\n");
            sb.Append("INSERT INTO ").Append(SyncStateTable).Append(" (server_version) VALUES (0);\n");

            foreach (var table in registry.TablesFor(version))
            {
                var columns = new List<string> { SchemaRegistry.IdColumn + " TEXT NOT NULL PRIMARY KEY" };
                columns.AddRange(table.FieldsFor(version).Select(ColumnDefinition));
                columns.Add(SchemaRegistry.RowVersionColumn + " INTEGER NOT NULL DEFAULT 0");
                columns.Add(SchemaRegistry.IsDeletedColumn + " INTEGER NOT NULL DEFAULT 0");

                sb.Append("CREATE TABLE ").Append(table.Name).Append(" (\n    ")
                  .Append(string.Join(",\n    ", columns))
                  .Append("\n);\n");

                foreach (var field in table.FieldsFor(version).Where(f => f.References != null))
                {
                    sb.Append("CREATE INDEX ix_").Append(table.Name).Append('_').Append(field.Name)
                      .Append(" ON ").Append(table.Name).Append(" (").Append(field.Name).Append(");\n");
                }
            }

            sb.Append("INSERT INTO ").Append(SchemaInfoTable).Append(" (version) VALUES (")
              .Append(version.ToString(CultureInfo.InvariantCulture)).Append(");\n");
            return sb.ToString();
        }

        public static void EnsureCreated(SqliteConnection connection, SchemaRegistry registry, int version)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var existing = ReadVersion(connection);
            if (existing == version) return;
            if (existing != null)
            {
                throw new NotSupportedException(
                    "Local database has schema v" + existing + ", requested v" + version + "; upgrades are not supported yet.");
            }

            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = BuildCreateScript(registry, version);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        public static int? ReadVersion(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $name";
            command.Parameters.AddWithValue("$name", SchemaInfoTable);
            if (command.ExecuteScalar() == null) return null;

            command.CommandText = "SELECT version FROM " + SchemaInfoTable;
            command.Parameters.Clear();
            var value = command.ExecuteScalar();
            return value == null ? (int?)null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        public static string SqlType(FieldType type)
        {
            switch (type)
            {
                case FieldType.Text:
                case FieldType.Uuid:
                    return "TEXT";
                case FieldType.Integer:
                case FieldType.Boolean:
                    return "INTEGER";
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        private static string ColumnDefinition(FieldDefinition field)
        {
            var sb = new StringBuilder();
            sb.Append(field.Name).Append(' ').Append(SqlType(field.Type));
            if (!field.IsNullable) sb.Append(" NOT NULL");
            if (field.DefaultValue != null) sb.Append(" DEFAULT ").Append(Literal(field.DefaultValue));
            if (field.References != null)
                sb.Append(" REFERENCES ").Append(field.References).Append(" (").Append(SchemaRegistry.IdColumn).Append(')');
            return sb.ToString();
        }

        private static string Literal(object value)
        {
            switch (value)
            {
                case bool b: return b ? "1" : "0";
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case string s: return "'" + s.Replace("'", "''") + "'";
                default: throw new NotSupportedException("Unsupported default value type " + value.GetType() + ".");
            }
        }
    }
}
