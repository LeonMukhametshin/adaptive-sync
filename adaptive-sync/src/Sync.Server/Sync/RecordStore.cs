using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Strategies;

namespace Sync.Server.Sync;

public sealed class RecordStore(NpgsqlConnection connection, NpgsqlTransaction transaction, SchemaRegistry registry)
{
    public async Task<ServerRecord?> LoadForUpdateAsync(TableDefinition table, Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {SelectList(table)} FROM {table.Name} WHERE {SchemaRegistry.IdColumn} = $1 FOR UPDATE";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var record = ReadRecord(table, reader);
        await reader.CloseAsync();
        record.DependentTables = await DependentTablesAsync(table, id, ct);
        record.HasDependents = record.DependentTables.Count > 0;
        return record;
    }

    public async Task<long> NextRowVersionAsync(CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT nextval('sync_row_version_seq')", connection, transaction);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> CurrentServerVersionAsync(CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM sync_row_version_seq", connection, transaction);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task InsertAsync(
        TableDefinition table, Guid id, IReadOnlyDictionary<string, object?> writes, long rowVersion, long logicalTime, CancellationToken ct)
    {
        var columns = new List<string> { SchemaRegistry.IdColumn, SchemaRegistry.RowVersionColumn, SchemaRegistry.FieldVersionsColumn };
        await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(rowVersion);
        command.Parameters.Add(new NpgsqlParameter { Value = FieldVersionsPatch(writes.Keys, rowVersion, logicalTime), NpgsqlDbType = NpgsqlDbType.Jsonb });

        foreach (var (name, value) in writes)
        {
            columns.Add(name);
            command.Parameters.Add(Parameter(table.FindField(name)!, value));
        }

        var placeholders = Enumerable.Range(1, columns.Count).Select(i => "$" + i);
        command.CommandText = $"INSERT INTO {table.Name} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", placeholders)})";
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(
        TableDefinition table, Guid id, IReadOnlyDictionary<string, object?> writes, long rowVersion, long logicalTime,
        long? deletedAt, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(rowVersion);
        command.Parameters.Add(new NpgsqlParameter { Value = FieldVersionsPatch(writes.Keys, rowVersion, logicalTime), NpgsqlDbType = NpgsqlDbType.Jsonb });

        var assignments = new List<string>
        {
            $"{SchemaRegistry.RowVersionColumn} = $2",
            $"{SchemaRegistry.FieldVersionsColumn} = {SchemaRegistry.FieldVersionsColumn} || $3",
        };

        foreach (var (name, value) in writes)
        {
            command.Parameters.Add(Parameter(table.FindField(name)!, value));
            assignments.Add($"{name} = ${command.Parameters.Count}");
        }

        if (deletedAt.HasValue)
        {
            command.Parameters.AddWithValue(deletedAt.Value);
            assignments.Add($"{SchemaRegistry.IsDeletedColumn} = true");
            assignments.Add($"{SchemaRegistry.DeletedAtColumn} = ${command.Parameters.Count}");
        }

        command.CommandText = $"UPDATE {table.Name} SET {string.Join(", ", assignments)} WHERE {SchemaRegistry.IdColumn} = $1";
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<RecordState> LoadStateAsync(TableDefinition table, Guid id, int clientVersion, CancellationToken ct)
    {
        var sql = $"SELECT {SelectList(table)} FROM {table.Name} WHERE {SchemaRegistry.IdColumn} = $1";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? Project(ReadRecord(table, reader), table, clientVersion)
            : new RecordState { Table = table.Name, RecordId = id, IsDeleted = true };
    }

    public async Task<List<RecordState>> LoadChangesSinceAsync(int clientVersion, long sinceServerVersion, CancellationToken ct)
    {
        var states = new List<RecordState>();
        foreach (var table in registry.TablesFor(clientVersion))
        {
            var sql = $"SELECT {SelectList(table)} FROM {table.Name} WHERE {SchemaRegistry.RowVersionColumn} > $1 " +
                      $"ORDER BY {SchemaRegistry.RowVersionColumn}";
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(sinceServerVersion);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                states.Add(Project(ReadRecord(table, reader), table, clientVersion));
        }
        return states;
    }

    public async Task<OperationResult?> FindLoggedResultAsync(Guid opId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT outcome, row_version, message FROM sync_operation_log WHERE op_id = $1", connection, transaction);
        command.Parameters.AddWithValue(opId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new OperationResult
        {
            OpId = opId,
            Outcome = Enum.Parse<OperationOutcome>(reader.GetString(0)),
            RowVersion = reader.GetInt64(1),
            Message = reader.IsDBNull(2) ? null : reader.GetString(2),
        };
    }

    public async Task LogAsync(
        SyncRequest request, SyncOperation operation, string strategy, OperationResult result,
        IReadOnlyList<FieldDecision> fields, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO sync_operation_log (op_id, client_id, schema_version, table_name, record_id, op_type, logical_time, " +
            "strategy, outcome, row_version, message, fields) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)",
            connection, transaction);
        command.Parameters.AddWithValue(operation.OpId);
        command.Parameters.AddWithValue(request.ClientId);
        command.Parameters.AddWithValue(request.SchemaVersion);
        command.Parameters.AddWithValue(operation.Table);
        command.Parameters.AddWithValue(operation.RecordId);
        command.Parameters.AddWithValue(operation.Type.ToString());
        command.Parameters.AddWithValue(operation.LogicalTime);
        command.Parameters.AddWithValue(strategy);
        command.Parameters.AddWithValue(result.Outcome.ToString());
        command.Parameters.AddWithValue(result.RowVersion);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)result.Message ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = JsonSerializer.Serialize(
                fields.Select(f => new { field = f.Field, outcome = f.Outcome.ToString(), situation = f.Situation?.ToString() })),
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<List<string>> DependentTablesAsync(TableDefinition table, Guid id, CancellationToken ct)
    {
        var tables = new List<string>();
        foreach (var child in registry.Tables)
        {
            foreach (var field in child.Fields.Where(f => f.References == table.Name))
            {
                var sql = $"SELECT EXISTS (SELECT 1 FROM {child.Name} WHERE {field.Name} = $1 AND NOT {SchemaRegistry.IsDeletedColumn})";
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue(id);
                if ((bool)(await command.ExecuteScalarAsync(ct))! && !tables.Contains(child.Name))
                    tables.Add(child.Name);
            }
        }
        return tables;
    }

    private static string SelectList(TableDefinition table) =>
        string.Join(", ", new[] { SchemaRegistry.IdColumn, SchemaRegistry.RowVersionColumn, SchemaRegistry.IsDeletedColumn,
            SchemaRegistry.FieldVersionsColumn + "::text" }.Concat(table.Fields.Select(f => f.Name)));

    private static ServerRecord ReadRecord(TableDefinition table, NpgsqlDataReader reader)
    {
        var record = new ServerRecord
        {
            Table = table.Name,
            RecordId = reader.GetGuid(0),
            RowVersion = reader.GetInt64(1),
            IsDeleted = reader.GetBoolean(2),
            FieldVersions = ParseFieldVersions(reader.GetString(3)),
        };
        for (var i = 0; i < table.Fields.Count; i++)
            record.Values[table.Fields[i].Name] = reader.IsDBNull(4 + i) ? null : reader.GetValue(4 + i);
        return record;
    }

    private static RecordState Project(ServerRecord record, TableDefinition table, int clientVersion) =>
        new()
        {
            Table = table.Name,
            RecordId = record.RecordId,
            RowVersion = record.RowVersion,
            IsDeleted = record.IsDeleted,
            Values = table.FieldsFor(clientVersion).ToDictionary(f => f.Name, f => record.Values[f.Name]),
        };

    private static Dictionary<string, FieldVersion> ParseFieldVersions(string json)
    {
        var result = new Dictionary<string, FieldVersion>();
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = new FieldVersion(
                property.Value.GetProperty("v").GetInt64(), property.Value.GetProperty("t").GetInt64());
        }
        return result;
    }

    private static string FieldVersionsPatch(IEnumerable<string> fields, long rowVersion, long logicalTime) =>
        JsonSerializer.Serialize(fields.ToDictionary(f => f, _ => new { v = rowVersion, t = logicalTime }));

    private static NpgsqlParameter Parameter(FieldDefinition field, object? value) =>
        new()
        {
            Value = FieldValues.Normalize(field.Type, value) ?? DBNull.Value,
            NpgsqlDbType = field.Type switch
            {
                FieldType.Text => NpgsqlDbType.Text,
                FieldType.Integer => NpgsqlDbType.Bigint,
                FieldType.Boolean => NpgsqlDbType.Boolean,
                FieldType.Uuid => NpgsqlDbType.Uuid,
                _ => throw new ArgumentOutOfRangeException(nameof(field)),
            },
        };
}
