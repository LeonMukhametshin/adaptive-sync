using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Oracle;

namespace Sync.Simulation;

public static class CounterDeltas
{
    public static void MakeConsistent(SchemaRegistry registry, IReadOnlyList<OracleRecord> initial, IReadOnlyList<OracleEvent> events)
    {
        var walk = new Walk(registry, initial);
        var own = new Dictionary<(string Author, long Seen, string Table, Guid Id, string Field), long>();
        foreach (var e in events)
        {
            walk.Apply(e, (field, change, current, history) =>
            {
                var delta = Delta(change);
                long old;
                if (e.IsServer)
                {
                    old = current;
                }
                else
                {
                    var key = (e.Author, e.SeenAt, e.Table, e.RecordId, field.Name);
                    own.TryGetValue(key, out var pending);
                    old = ValueAt(history, e.SeenAt) + pending;
                    own[key] = pending + delta;
                }
                change.OldValue = old;
                change.NewValue = old + delta;
                return delta;
            });
        }
    }

    public static Dictionary<(string Table, Guid Id, string Field), long> ExpectedTotals(
        SchemaRegistry registry, IReadOnlyList<OracleRecord> initial, IReadOnlyList<OracleEvent> events)
    {
        var walk = new Walk(registry, initial);
        foreach (var e in events)
            walk.Apply(e, (_, change, _, _) => Delta(change));
        return walk.Totals();
    }

    private static long Delta(FieldChange change) => AsLong(change.NewValue) - AsLong(change.OldValue);

    private static long AsLong(object? value) => value == null ? 0L : Convert.ToInt64(FieldValues.Normalize(FieldType.Integer, value));

    private static long ValueAt(List<(long DeliveredAt, long Value)> history, long time)
    {
        var value = history[0].Value;
        foreach (var (deliveredAt, v) in history)
        {
            if (deliveredAt > time) break;
            value = v;
        }
        return value;
    }

    private sealed class Walk
    {
        private readonly SchemaRegistry _registry;
        private readonly Dictionary<(string Table, Guid Id), bool> _alive = new();
        private readonly Dictionary<(string Table, Guid Id, string Field), List<(long DeliveredAt, long Value)>> _history = new();

        public Walk(SchemaRegistry registry, IReadOnlyList<OracleRecord> initial)
        {
            _registry = registry;
            foreach (var record in initial)
            {
                _alive[(record.Table, record.RecordId)] = !record.IsDeleted;
                foreach (var field in Counters(record.Table))
                {
                    var value = record.Values.TryGetValue(field.Name, out var v) ? AsLong(v) : AsLong(field.DefaultValue);
                    _history[(record.Table, record.RecordId, field.Name)] = new() { (long.MinValue, value) };
                }
            }
        }

        public void Apply(
            OracleEvent e,
            Func<FieldDefinition, FieldChange, long, List<(long DeliveredAt, long Value)>, long> counterChange)
        {
            var key = (e.Table, e.RecordId);
            var exists = _alive.TryGetValue(key, out var alive);
            switch (e.Operation)
            {
                case OracleOperation.Delete:
                    if (exists && alive) _alive[key] = false;
                    return;
                case OracleOperation.Insert when !exists:
                    _alive[key] = true;
                    foreach (var field in Counters(e.Table))
                    {
                        var change = e.Changes.FirstOrDefault(c => c.Field == field.Name);
                        var value = change != null ? AsLong(change.NewValue) : AsLong(field.DefaultValue);
                        _history[(e.Table, e.RecordId, field.Name)] = new() { (long.MinValue, AsLong(field.DefaultValue)), (e.DeliveredAt, value) };
                    }
                    return;
            }

            if (!exists || !alive) return;
            foreach (var change in e.Changes)
            {
                var field = _registry.GetTable(e.Table).FindField(change.Field)!;
                if (field.Category != FieldCategory.Counter) continue;
                var history = _history[(e.Table, e.RecordId, field.Name)];
                var current = history[^1].Value;
                var delta = counterChange(field, change, current, history);
                history.Add((e.DeliveredAt, current + delta));
            }
        }

        public Dictionary<(string Table, Guid Id, string Field), long> Totals() =>
            _history.ToDictionary(p => p.Key, p => p.Value[^1].Value);

        private IEnumerable<FieldDefinition> Counters(string table) =>
            _registry.GetTable(table).Fields.Where(f => f.Category == FieldCategory.Counter);
    }
}
