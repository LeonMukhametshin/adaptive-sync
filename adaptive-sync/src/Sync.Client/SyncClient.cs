using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Sync.Client.Storage;
using Sync.Client.Transport;
using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Time;

namespace Sync.Client
{
    public sealed class SyncClient : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ISyncTransport _transport;
        private readonly LogicalClock _clock;
        private readonly Func<Guid> _newId;
        private readonly bool _ownsConnection;

        public SyncClient(
            string clientId,
            int schemaVersion,
            SqliteConnection connection,
            ISyncTransport transport,
            LogicalClock clock,
            Func<Guid> newId,
            SchemaRegistry? registry = null,
            string appVersion = "1.0.0",
            bool ownsConnection = false)
        {
            if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Client id is required.", nameof(clientId));
            ClientId = clientId;
            Registry = registry ?? SchemaVersions.Registry;
            if (!Registry.IsSupported(schemaVersion))
                throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "Unsupported schema version.");
            SchemaVersion = schemaVersion;
            AppVersion = appVersion;
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _newId = newId ?? throw new ArgumentNullException(nameof(newId));
            _ownsConnection = ownsConnection;

            if (_connection.State != System.Data.ConnectionState.Open) _connection.Open();
            Execute("PRAGMA foreign_keys = ON");
            SqliteSchema.EnsureCreated(_connection, Registry, SchemaVersion);
        }

        public static SyncClient Open(
            string databasePath, string clientId, int schemaVersion, ISyncTransport transport, LogicalClock clock,
            Func<Guid> newId, SchemaRegistry? registry = null)
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true, Pooling = false };
            return new SyncClient(clientId, schemaVersion, new SqliteConnection(builder.ConnectionString), transport, clock, newId,
                registry, ownsConnection: true);
        }

        public string ClientId { get; }

        public string AppVersion { get; }

        public int SchemaVersion { get; }

        public SchemaRegistry Registry { get; }

        public bool IsOnline { get; set; } = true;

        public long ServerVersion => Convert.ToInt64(Scalar("SELECT server_version FROM " + SqliteSchema.SyncStateTable), CultureInfo.InvariantCulture);

        public int PendingOperationCount => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM " + SqliteSchema.JournalTable), CultureInfo.InvariantCulture);

        public Guid Insert(string table, IReadOnlyDictionary<string, object?> values)
        {
            var definition = KnownTable(table);
            var changes = values
                .Select(p => new FieldChange { Field = p.Key, NewValue = FieldValues.Normalize(KnownField(definition, p.Key).Type, p.Value) })
                .ToList();
            var id = _newId();

            using var transaction = _connection.BeginTransaction();
            using (var command = Command(transaction))
            {
                var columns = new List<string> { SchemaRegistry.IdColumn };
                command.Parameters.AddWithValue("$p0", FormatId(id));
                foreach (var change in changes)
                {
                    columns.Add(change.Field);
                    command.Parameters.AddWithValue("$p" + (columns.Count - 1),
                        FieldValues.ToStorage(definition.FindField(change.Field)!.Type, change.NewValue) ?? DBNull.Value);
                }
                command.CommandText = "INSERT INTO " + table + " (" + string.Join(", ", columns) + ") VALUES (" +
                                      string.Join(", ", columns.Select((_, i) => "$p" + i)) + ")";
                command.ExecuteNonQuery();
            }

            Journal(transaction, table, id, OperationType.Insert, 0, changes);
            transaction.Commit();
            return id;
        }

        public void Update(string table, Guid id, IReadOnlyDictionary<string, object?> values)
        {
            var definition = KnownTable(table);
            var current = Find(table, id);
            if (current == null || current.IsDeleted)
                throw new InvalidOperationException("Record " + table + "/" + id + " does not exist locally.");

            var changes = new List<FieldChange>();
            foreach (var pair in values)
            {
                var type = KnownField(definition, pair.Key).Type;
                var newValue = FieldValues.Normalize(type, pair.Value);
                var oldValue = current.Values[pair.Key];
                if (!Equals(oldValue, newValue))
                    changes.Add(new FieldChange { Field = pair.Key, OldValue = oldValue, NewValue = newValue });
            }
            if (changes.Count == 0) return;

            using var transaction = _connection.BeginTransaction();
            using (var command = Command(transaction))
            {
                command.Parameters.AddWithValue("$id", FormatId(id));
                var assignments = new List<string>();
                for (var i = 0; i < changes.Count; i++)
                {
                    assignments.Add(changes[i].Field + " = $p" + i);
                    command.Parameters.AddWithValue("$p" + i,
                        FieldValues.ToStorage(definition.FindField(changes[i].Field)!.Type, changes[i].NewValue) ?? DBNull.Value);
                }
                command.CommandText = "UPDATE " + table + " SET " + string.Join(", ", assignments) + " WHERE " + SchemaRegistry.IdColumn + " = $id";
                command.ExecuteNonQuery();
            }

            Journal(transaction, table, id, OperationType.Update, current.RowVersion, changes);
            transaction.Commit();
        }

        public void Delete(string table, Guid id)
        {
            KnownTable(table);
            var current = Find(table, id);
            if (current == null || current.IsDeleted)
                throw new InvalidOperationException("Record " + table + "/" + id + " does not exist locally.");

            using var transaction = _connection.BeginTransaction();
            using (var command = Command(transaction))
            {
                command.CommandText = "UPDATE " + table + " SET " + SchemaRegistry.IsDeletedColumn + " = 1 WHERE " + SchemaRegistry.IdColumn + " = $id";
                command.Parameters.AddWithValue("$id", FormatId(id));
                command.ExecuteNonQuery();
            }

            Journal(transaction, table, id, OperationType.Delete, current.RowVersion, new List<FieldChange>());
            transaction.Commit();
        }

        public RecordState? Find(string table, Guid id) =>
            Query(KnownTable(table), " WHERE " + SchemaRegistry.IdColumn + " = $id", ("$id", FormatId(id))).FirstOrDefault();

        public IReadOnlyList<RecordState> GetAll(string table, bool includeDeleted = false) =>
            Query(KnownTable(table), includeDeleted ? "" : " WHERE " + SchemaRegistry.IsDeletedColumn + " = 0");

        public IReadOnlyList<SyncOperation> PendingOperations()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT op_id, table_name, record_id, op_type, base_row_version, changes, logical_time FROM " +
                                  SqliteSchema.JournalTable + " ORDER BY seq";
            using var reader = command.ExecuteReader();
            var operations = new List<SyncOperation>();
            while (reader.Read())
            {
                var table = Registry.GetTable(reader.GetString(1));
                var changes = JsonSerializer.Deserialize<List<FieldChange>>(reader.GetString(5), SyncJson.Options) ?? new List<FieldChange>();
                foreach (var change in changes)
                {
                    var type = table.FindField(change.Field)!.Type;
                    change.OldValue = FieldValues.Normalize(type, change.OldValue);
                    change.NewValue = FieldValues.Normalize(type, change.NewValue);
                }

                operations.Add(new SyncOperation
                {
                    OpId = Guid.Parse(reader.GetString(0)),
                    Table = table.Name,
                    RecordId = Guid.Parse(reader.GetString(2)),
                    Type = (OperationType)Enum.Parse(typeof(OperationType), reader.GetString(3)),
                    BaseRowVersion = reader.GetInt64(4),
                    Changes = changes,
                    LogicalTime = reader.GetInt64(6),
                });
            }
            return operations;
        }

        public SyncRequest BuildRequest() =>
            new SyncRequest
            {
                ClientId = ClientId,
                AppVersion = AppVersion,
                SchemaVersion = SchemaVersion,
                SinceServerVersion = ServerVersion,
                Operations = PendingOperations().ToList(),
            };

        public async Task<SyncResponse> SyncAsync(CancellationToken cancellationToken = default)
        {
            if (!IsOnline) throw new InvalidOperationException("Client " + ClientId + " is offline.");

            var request = BuildRequest();
            var response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ApplyResponse(response);
            return response;
        }

        public void Dispose()
        {
            if (_ownsConnection) _connection.Dispose();
        }

        private void ApplyResponse(SyncResponse response)
        {
            var tableOrder = Registry.Tables.Select((t, i) => (t.Name, i)).ToDictionary(p => p.Name, p => p.i);
            var states = response.Records.Concat(response.RemoteChanges)
                .Where(s => tableOrder.ContainsKey(s.Table) && Registry.TablesFor(SchemaVersion).Any(t => t.Name == s.Table))
                .OrderBy(s => tableOrder[s.Table])
                .ThenBy(s => s.RowVersion)
                .ToList();

            using var transaction = _connection.BeginTransaction();
            foreach (var state in states)
                ApplyState(transaction, state);

            foreach (var result in response.Results)
            {
                using var command = Command(transaction);
                command.CommandText = "DELETE FROM " + SqliteSchema.JournalTable + " WHERE op_id = $op";
                command.Parameters.AddWithValue("$op", FormatId(result.OpId));
                command.ExecuteNonQuery();
            }

            using (var command = Command(transaction))
            {
                command.CommandText = "UPDATE " + SqliteSchema.SyncStateTable + " SET server_version = MAX(server_version, $v)";
                command.Parameters.AddWithValue("$v", response.ServerVersion);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        private void ApplyState(SqliteTransaction transaction, RecordState state)
        {
            var table = Registry.GetTable(state.Table);
            using var command = Command(transaction);
            command.Parameters.AddWithValue("$id", FormatId(state.RecordId));
            command.Parameters.AddWithValue("$rv", state.RowVersion);

            if (state.IsDeleted)
            {
                command.CommandText = "UPDATE " + table.Name + " SET " + SchemaRegistry.IsDeletedColumn + " = 1, " +
                                      SchemaRegistry.RowVersionColumn + " = MAX(" + SchemaRegistry.RowVersionColumn + ", $rv) WHERE " +
                                      SchemaRegistry.IdColumn + " = $id";
                command.ExecuteNonQuery();
                return;
            }

            var fields = table.FieldsFor(SchemaVersion).Where(f => state.Values.ContainsKey(f.Name)).ToList();
            for (var i = 0; i < fields.Count; i++)
                command.Parameters.AddWithValue("$p" + i, FieldValues.ToStorage(fields[i].Type, state.Values[fields[i].Name]) ?? DBNull.Value);

            var columns = new[] { SchemaRegistry.IdColumn }.Concat(fields.Select(f => f.Name))
                .Concat(new[] { SchemaRegistry.RowVersionColumn, SchemaRegistry.IsDeletedColumn });
            var values = new[] { "$id" }.Concat(fields.Select((_, i) => "$p" + i)).Concat(new[] { "$rv", "0" });
            var updates = fields.Select(f => f.Name + " = excluded." + f.Name)
                .Concat(new[]
                {
                    SchemaRegistry.RowVersionColumn + " = excluded." + SchemaRegistry.RowVersionColumn,
                    SchemaRegistry.IsDeletedColumn + " = 0",
                });

            command.CommandText = "INSERT INTO " + table.Name + " (" + string.Join(", ", columns) + ") VALUES (" +
                                  string.Join(", ", values) + ") ON CONFLICT(" + SchemaRegistry.IdColumn + ") DO UPDATE SET " +
                                  string.Join(", ", updates);
            command.ExecuteNonQuery();
        }

        private void Journal(
            SqliteTransaction transaction, string table, Guid recordId, OperationType type, long baseRowVersion, List<FieldChange> changes)
        {
            using var command = Command(transaction);
            command.CommandText = "INSERT INTO " + SqliteSchema.JournalTable +
                                  " (op_id, table_name, record_id, op_type, base_row_version, changes, logical_time) " +
                                  "VALUES ($op, $table, $record, $type, $base, $changes, $time)";
            command.Parameters.AddWithValue("$op", FormatId(_newId()));
            command.Parameters.AddWithValue("$table", table);
            command.Parameters.AddWithValue("$record", FormatId(recordId));
            command.Parameters.AddWithValue("$type", type.ToString());
            command.Parameters.AddWithValue("$base", baseRowVersion);
            command.Parameters.AddWithValue("$changes", JsonSerializer.Serialize(changes, SyncJson.Options));
            command.Parameters.AddWithValue("$time", _clock.Tick());
            command.ExecuteNonQuery();
        }

        private List<RecordState> Query(TableDefinition table, string where, params (string Name, object Value)[] parameters)
        {
            var fields = table.FieldsFor(SchemaVersion).ToList();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT " + SchemaRegistry.IdColumn + ", " + SchemaRegistry.RowVersionColumn + ", " +
                                  SchemaRegistry.IsDeletedColumn + string.Concat(fields.Select(f => ", " + f.Name)) +
                                  " FROM " + table.Name + where + " ORDER BY " + SchemaRegistry.IdColumn;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);

            using var reader = command.ExecuteReader();
            var states = new List<RecordState>();
            while (reader.Read())
            {
                var state = new RecordState
                {
                    Table = table.Name,
                    RecordId = Guid.Parse(reader.GetString(0)),
                    RowVersion = reader.GetInt64(1),
                    IsDeleted = reader.GetInt64(2) != 0,
                };
                for (var i = 0; i < fields.Count; i++)
                    state.Values[fields[i].Name] = FieldValues.Normalize(fields[i].Type, reader.IsDBNull(3 + i) ? null : reader.GetValue(3 + i));
                states.Add(state);
            }
            return states;
        }

        private TableDefinition KnownTable(string table)
        {
            var definition = Registry.TablesFor(SchemaVersion).FirstOrDefault(t => t.Name == table);
            return definition ?? throw new ArgumentException("Table '" + table + "' is not part of schema v" + SchemaVersion + ".", nameof(table));
        }

        private FieldDefinition KnownField(TableDefinition table, string field)
        {
            if (!Registry.IsKnown(SchemaVersion, table.Name, field))
                throw new ArgumentException("Field '" + table.Name + "." + field + "' is outside K(v" + SchemaVersion + ").", nameof(field));
            return table.FindField(field)!;
        }

        private SqliteCommand Command(SqliteTransaction transaction)
        {
            var command = _connection.CreateCommand();
            command.Transaction = transaction;
            return command;
        }

        private void Execute(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private object? Scalar(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        private static string FormatId(Guid id) => id.ToString("D");
    }
}
