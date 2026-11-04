using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Strategies;
using Sync.Oracle;

namespace Sync.Simulation;

public sealed class ReplayRecord
{
    public string Table { get; init; } = string.Empty;

    public Guid RecordId { get; init; }

    public long RowVersion { get; set; }

    public bool IsDeleted { get; set; }

    public bool IsTombstone { get; set; }

    public Dictionary<string, object?> Values { get; } = new();

    public Dictionary<string, FieldVersion> FieldVersions { get; } = new();
}

public sealed record RecordSnapshot(IReadOnlyDictionary<string, object?> Values, bool IsDeleted, bool IsTombstone);

public sealed record ReplayStep(
    int EventIndex, OracleEvent Event, SyncOperation Operation, ServerRecord? Current, StrategyDecision Decision, RecordSnapshot? After);

public sealed class ReplayResult
{
    public ReplayResult(IReadOnlyDictionary<(string Table, Guid Id), ReplayRecord> records, IReadOnlyList<ReplayStep> steps)
    {
        Records = records;
        Steps = steps;
    }

    public IReadOnlyDictionary<(string Table, Guid Id), ReplayRecord> Records { get; }

    public IReadOnlyList<ReplayStep> Steps { get; }

    public ReplayRecord Record(string table, Guid id) =>
        Records.TryGetValue((table, id), out var record)
            ? record
            : throw new KeyNotFoundException($"Replay has no record {table}/{id}.");
}

public sealed class StrategyReplay(SchemaRegistry registry)
{
    public ReplayResult Run(OracleInput input, ISyncStrategy strategy)
    {
        var records = new Dictionary<(string Table, Guid Id), ReplayRecord>();
        var steps = new List<ReplayStep>();
        var deliveries = new List<(long DeliveredAt, long Version)>();
        var own = new Dictionary<(string Author, long Seen, long Delivered, string Table, Guid Id), OwnWrites>();
        long version = 0;

        foreach (var initial in input.InitialRecords)
        {
            var table = registry.GetTable(initial.Table);
            var record = Create(table, initial.RecordId, ++version);
            foreach (var field in table.Fields)
                record.FieldVersions[field.Name] = new FieldVersion(version, 0);
            foreach (var (name, value) in initial.Values)
                record.Values[name] = FieldValues.Normalize(table.FindField(name)!.Type, value);
            record.IsDeleted = initial.IsDeleted;
            records[(table.Name, initial.RecordId)] = record;
        }
        var initialVersion = version;

        for (var index = 0; index < input.Events.Count; index++)
        {
            var e = input.Events[index];
            var table = registry.GetTable(e.Table);
            var key = (table.Name, e.RecordId);
            records.TryGetValue(key, out var current);

            if (e.IsServer)
            {
                if (ApplyServerEvent(table, e, current, version + 1, records))
                    version++;
            }
            else
            {
                var seen = deliveries.Where(d => d.DeliveredAt <= e.SeenAt).Select(d => d.Version).DefaultIfEmpty(initialVersion).Max();
                var operation = ToOperation(table, e, index, seen);
                var batch = (e.Author, e.SeenAt, e.DeliveredAt, table.Name, e.RecordId);
                if (!own.TryGetValue(batch, out var ownWrites))
                    own[batch] = ownWrites = new OwnWrites();

                var context = new SyncContext(registry, e.Author, e.SchemaVersion);
                var snapshot = current == null ? null : ownWrites.Mask(Snapshot(table, current, records), operation.BaseRowVersion);
                var decision = strategy.Decide(context, operation, snapshot);

                if (decision.Outcome != OperationOutcome.Rejected)
                {
                    version++;
                    ApplyDecision(table, operation, current, decision, version, records);
                    ownWrites.Add(version);
                }

                steps.Add(new ReplayStep(index, e, operation, snapshot, decision, records.TryGetValue(key, out var after)
                    ? new RecordSnapshot(new Dictionary<string, object?>(after.Values), after.IsDeleted, after.IsTombstone)
                    : null));
            }
            deliveries.Add((e.DeliveredAt, version));
        }

        return new ReplayResult(records, steps);
    }

    private static bool ApplyServerEvent(
        TableDefinition table, OracleEvent e, ReplayRecord? current, long version,
        Dictionary<(string Table, Guid Id), ReplayRecord> records)
    {
        if (current == null || current.IsDeleted)
        {
            if (e.Operation != OracleOperation.Insert || current != null) return false;
            current = Create(table, e.RecordId, version);
            records[(table.Name, e.RecordId)] = current;
        }

        if (e.Operation == OracleOperation.Delete)
            current.IsDeleted = true;
        foreach (var change in e.Changes)
            Write(table, current, change.Field, change.NewValue, version, e.MadeAt);
        current.RowVersion = version;
        return true;
    }

    private static void ApplyDecision(
        TableDefinition table, SyncOperation operation, ReplayRecord? current, StrategyDecision decision, long version,
        Dictionary<(string Table, Guid Id), ReplayRecord> records)
    {
        if (current == null)
        {
            if (decision.HardDelete || decision.Tombstone)
                throw new InvalidOperationException("Strategy deleted a record that does not exist.");
            current = Create(table, operation.RecordId, version);
            records[(table.Name, operation.RecordId)] = current;
        }

        foreach (var (field, value) in decision.FieldWrites)
        {
            if (table.FindField(field) == null)
                throw new InvalidOperationException($"Strategy wrote unknown field '{table.Name}.{field}'.");
            Write(table, current, field, value, version, operation.LogicalTime);
        }

        if (decision.HardDelete || decision.Tombstone)
        {
            current.IsDeleted = true;
            current.IsTombstone = decision.Tombstone;
        }
        current.RowVersion = version;
    }

    private static SyncOperation ToOperation(TableDefinition table, OracleEvent e, int index, long baseVersion) =>
        new()
        {
            OpId = new Guid(index + 1, 0, 0, new byte[8]),
            Table = table.Name,
            RecordId = e.RecordId,
            Type = e.Operation switch
            {
                OracleOperation.Insert => OperationType.Insert,
                OracleOperation.Update => OperationType.Update,
                _ => OperationType.Delete,
            },
            BaseRowVersion = baseVersion,
            LogicalTime = e.MadeAt,
            Changes = e.Changes
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

    private ServerRecord Snapshot(TableDefinition table, ReplayRecord record, Dictionary<(string Table, Guid Id), ReplayRecord> records)
    {
        var dependents = registry.Tables
            .SelectMany(child => child.Fields.Where(f => f.References == table.Name).Select(f => (child, f)))
            .Where(link => records.Values.Any(r =>
                r.Table == link.child.Name && !r.IsDeleted && Equals(r.Values[link.f.Name], record.RecordId)))
            .Select(link => link.child.Name)
            .Distinct()
            .ToList();

        return new ServerRecord
        {
            Table = table.Name,
            RecordId = record.RecordId,
            RowVersion = record.RowVersion,
            IsDeleted = record.IsDeleted,
            Values = new Dictionary<string, object?>(record.Values),
            FieldVersions = new Dictionary<string, FieldVersion>(record.FieldVersions),
            HasDependents = dependents.Count > 0,
            DependentTables = dependents,
        };
    }

    private static ReplayRecord Create(TableDefinition table, Guid id, long version)
    {
        var record = new ReplayRecord { Table = table.Name, RecordId = id, RowVersion = version };
        foreach (var field in table.Fields)
            record.Values[field.Name] = field.DefaultValue;
        return record;
    }

    private static void Write(TableDefinition table, ReplayRecord record, string field, object? value, long version, long time)
    {
        record.Values[field] = FieldValues.Normalize(table.FindField(field)!.Type, value);
        record.FieldVersions[field] = new FieldVersion(version, time);
    }
}
