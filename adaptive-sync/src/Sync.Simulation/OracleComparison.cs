using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Strategies;
using Sync.Oracle;

namespace Sync.Simulation;

public sealed record StateMismatch(string Table, Guid RecordId, string Field, object? Expected, object? Actual);

public static class OracleComparison
{
    public const string DeletedMarker = "(deleted)";
    public const string TombstoneMarker = "(tombstone)";

    public static IReadOnlyList<StateMismatch> Compare(SchemaRegistry registry, OracleResult expected, ReplayResult actual)
    {
        var mismatches = new List<StateMismatch>();
        foreach (var ((tableName, id), reference) in expected.Records.OrderBy(r => r.Key.Table).ThenBy(r => r.Key.Id))
        {
            var table = registry.GetTable(tableName);
            if (!actual.Records.TryGetValue((tableName, id), out var record))
            {
                mismatches.Add(new StateMismatch(tableName, id, DeletedMarker, reference.IsDeleted, null));
                continue;
            }

            if (reference.IsDeleted != record.IsDeleted)
                mismatches.Add(new StateMismatch(tableName, id, DeletedMarker, reference.IsDeleted, record.IsDeleted));
            if (reference.IsTombstone != record.IsTombstone)
                mismatches.Add(new StateMismatch(tableName, id, TombstoneMarker, reference.IsTombstone, record.IsTombstone));

            foreach (var field in table.Fields)
            {
                var want = reference.Values[field.Name];
                var got = record.Values[field.Name];
                if (!FieldValues.AreEqual(field.Type, want, got))
                    mismatches.Add(new StateMismatch(tableName, id, field.Name, want, got));
            }
        }
        return mismatches;
    }

    public static IReadOnlyList<(int EventIndex, string? Field, SyncSituation Situation)> Classifications(ReplayResult result)
    {
        var classified = new List<(int, string?, SyncSituation)>();
        foreach (var step in result.Steps)
        {
            if (step.Operation.Type == OperationType.Delete)
            {
                if (step.Decision.Situation.HasValue)
                    classified.Add((step.EventIndex, null, step.Decision.Situation.Value));
                continue;
            }

            foreach (var field in step.Decision.Fields.Where(f => f.Situation.HasValue))
                classified.Add((step.EventIndex, field.Field, field.Situation!.Value));
        }
        return classified;
    }
}
