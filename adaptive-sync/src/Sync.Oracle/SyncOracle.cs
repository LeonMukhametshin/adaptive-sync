using Sync.Core.Schema;
using Sync.Core.Strategies;

namespace Sync.Oracle;

public sealed class SyncOracle(SchemaRegistry registry)
{
    public OracleResult Run(OracleInput input) => Run(input, null);

    public OracleResult Run(OracleInput input, Action<int, ExpectedRecord>? afterClientEvent)
    {
        var state = new Dictionary<(string Table, Guid Id), RecordState>();
        foreach (var initial in input.InitialRecords)
            state[(initial.Table, initial.RecordId)] = RecordState.FromInitial(registry.GetTable(initial.Table), initial);

        var classifications = new List<ChangeClassification>();
        long previousDelivery = long.MinValue;
        for (var index = 0; index < input.Events.Count; index++)
        {
            var e = input.Events[index];
            Validate(e, index, previousDelivery);
            previousDelivery = e.DeliveredAt;

            var table = registry.GetTable(e.Table);
            state.TryGetValue((e.Table, e.RecordId), out var record);
            switch (e.Operation)
            {
                case OracleOperation.Delete:
                    if (record != null && !record.IsDeleted)
                        Delete(index, e, table, record, state, classifications);
                    break;
                default:
                    if (record == null || record.IsDeleted)
                    {
                        if (e.Operation == OracleOperation.Insert && record == null)
                        {
                            record = RecordState.Create(table, e.RecordId);
                            state[(e.Table, e.RecordId)] = record;
                        }
                        else
                        {
                            break;
                        }
                    }
                    Change(index, e, table, record, classifications);
                    break;
            }

            if (afterClientEvent != null && !e.IsServer && state.TryGetValue((e.Table, e.RecordId), out var after))
                afterClientEvent(index, after.ToExpected());
        }

        var records = state.ToDictionary(p => p.Key, p => p.Value.ToExpected());
        return new OracleResult(records, classifications);
    }

    private void Change(int index, OracleEvent e, TableDefinition table, RecordState record, List<ChangeClassification> classifications)
    {
        var olderClient = !e.IsServer && e.SchemaVersion < registry.LatestVersion;
        var unknownFieldsChanged = olderClient && record.History.Any(h =>
            h.DeliveredAt > e.SeenAt && !registry.IsKnown(e.SchemaVersion, table.Name, h.Field));

        foreach (var change in e.Changes)
        {
            var field = table.FindField(change.Field)!;
            var oldValue = FieldValues.Normalize(field.Type, change.OldValue);
            var newValue = FieldValues.Normalize(field.Type, change.NewValue);
            var conflict = !e.IsServer && record.History.Any(h =>
                h.Field == field.Name && h.Author != e.Author && h.DeliveredAt > e.SeenAt);

            var written = true;
            if (conflict)
            {
                (record.Values[field.Name], written) = ResolveConflict(field, record, e, oldValue, newValue);
            }
            else
            {
                record.Values[field.Name] = newValue;
                record.FieldMadeAt[field.Name] = e.MadeAt;
            }
            if (written)
                record.History.Add(new FieldDelivery(field.Name, e.Author, e.DeliveredAt));

            if (!e.IsServer)
            {
                var situation = conflict ? SyncSituation.S4
                    : !olderClient ? SyncSituation.S1
                    : unknownFieldsChanged ? SyncSituation.S3
                    : SyncSituation.S2;
                classifications.Add(new ChangeClassification(index, table.Name, record.Id, field.Name, situation));
            }
        }
    }

    private static (object? Value, bool Written) ResolveConflict(
        FieldDefinition field, RecordState record, OracleEvent e, object? oldValue, object? newValue)
    {
        var stored = record.Values[field.Name];
        switch (field.Category)
        {
            case FieldCategory.Counter:
                record.FieldMadeAt[field.Name] = Math.Max(record.FieldMadeAt[field.Name], e.MadeAt);
                return ((long)(stored ?? 0L) + ((long)(newValue ?? 0L) - (long)(oldValue ?? 0L)), true);
            case FieldCategory.UserOwned:
            case FieldCategory.UserPreference:
                record.FieldMadeAt[field.Name] = e.MadeAt;
                return (newValue, true);
            case FieldCategory.ServerAuthoritative:
                return (stored, false);
            case FieldCategory.Scalar:
                if (e.MadeAt <= record.FieldMadeAt[field.Name]) return (stored, false);
                record.FieldMadeAt[field.Name] = e.MadeAt;
                return (newValue, true);
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field.Category, null);
        }
    }

    private void Delete(
        int index, OracleEvent e, TableDefinition table, RecordState record,
        Dictionary<(string Table, Guid Id), RecordState> state, List<ChangeClassification> classifications)
    {
        var olderClient = !e.IsServer && e.SchemaVersion < registry.LatestVersion;
        var keepsUnknownData = olderClient &&
            (HasUnknownData(table, record, e.SchemaVersion) || HasDependentsInNewerTables(table, record, e.SchemaVersion, state));

        record.IsDeleted = true;
        record.IsTombstone = keepsUnknownData;

        if (!e.IsServer)
        {
            var situation = keepsUnknownData ? SyncSituation.S5 : olderClient ? SyncSituation.S2 : SyncSituation.S1;
            classifications.Add(new ChangeClassification(index, table.Name, record.Id, null, situation));
        }
    }

    private bool HasUnknownData(TableDefinition table, RecordState record, int version) =>
        table.Fields.Any(f => !registry.IsKnown(version, table.Name, f.Name) &&
                              !FieldValues.AreEqual(f.Type, record.Values[f.Name], f.DefaultValue));

    private bool HasDependentsInNewerTables(
        TableDefinition table, RecordState record, int version, Dictionary<(string Table, Guid Id), RecordState> state) =>
        registry.Tables
            .Where(child => child.IntroducedIn > version)
            .SelectMany(child => child.Fields.Where(f => f.References == table.Name).Select(f => (child, f)))
            .Any(link => state.Values.Any(r =>
                r.Table == link.child.Name && !r.IsDeleted && Equals(r.Values[link.f.Name], record.Id)));

    private void Validate(OracleEvent e, int index, long previousDelivery)
    {
        if (e.DeliveredAt < previousDelivery)
            throw new ArgumentException($"Event {index} is delivered at {e.DeliveredAt}, before the previous event ({previousDelivery}).");
        if (e.MadeAt > e.DeliveredAt || e.SeenAt > e.MadeAt)
            throw new ArgumentException($"Event {index} must satisfy SeenAt <= MadeAt <= DeliveredAt.");

        var version = e.IsServer ? registry.LatestVersion : e.SchemaVersion;
        if (!registry.TablesFor(version).Any(t => t.Name == e.Table))
            throw new ArgumentException($"Event {index}: table '{e.Table}' is not part of schema v{version}.");
        if (e.Operation == OracleOperation.Delete && e.Changes.Count > 0)
            throw new ArgumentException($"Event {index}: delete must not carry field changes.");
        foreach (var change in e.Changes)
        {
            if (!registry.IsKnown(version, e.Table, change.Field))
                throw new ArgumentException($"Event {index}: field '{e.Table}.{change.Field}' is outside K(v{version}).");
        }
    }

    private sealed record FieldDelivery(string Field, string Author, long DeliveredAt);

    private sealed class RecordState
    {
        private RecordState(string table, Guid id)
        {
            Table = table;
            Id = id;
        }

        public string Table { get; }

        public Guid Id { get; }

        public Dictionary<string, object?> Values { get; } = new();

        public Dictionary<string, long> FieldMadeAt { get; } = new();

        public List<FieldDelivery> History { get; } = new();

        public bool IsDeleted { get; set; }

        public bool IsTombstone { get; set; }

        public static RecordState Create(TableDefinition table, Guid id)
        {
            var record = new RecordState(table.Name, id);
            foreach (var field in table.Fields)
            {
                record.Values[field.Name] = field.DefaultValue;
                record.FieldMadeAt[field.Name] = 0;
            }
            return record;
        }

        public static RecordState FromInitial(TableDefinition table, OracleRecord initial)
        {
            var record = Create(table, initial.RecordId);
            foreach (var (name, value) in initial.Values)
            {
                var field = table.FindField(name) ?? throw new ArgumentException($"Unknown field '{table.Name}.{name}'.");
                record.Values[name] = FieldValues.Normalize(field.Type, value);
            }
            record.IsDeleted = initial.IsDeleted;
            return record;
        }

        public ExpectedRecord ToExpected() =>
            new()
            {
                Table = Table,
                RecordId = Id,
                IsDeleted = IsDeleted,
                IsTombstone = IsTombstone,
                Values = new Dictionary<string, object?>(Values),
            };
    }
}
