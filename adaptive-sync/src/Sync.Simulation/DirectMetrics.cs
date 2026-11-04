using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Oracle;

namespace Sync.Simulation;

public static class DirectMetrics
{
    public static StrategyMetrics Calculate(SchemaRegistry registry, OracleInput input, ReplayResult replay)
    {
        int alive = 0, rejected = 0, merged = 0, clientFields = 0, rejectedFields = 0, unknownWrites = 0, risky = 0, unprotected = 0;
        foreach (var step in replay.Steps)
        {
            var version = step.Event.SchemaVersion;
            var table = registry.GetTable(step.Operation.Table);
            var decision = step.Decision;

            if (decision.Outcome != OperationOutcome.Rejected)
                unknownWrites += decision.FieldWrites.Keys.Count(f => !registry.IsKnown(version, table.Name, f));

            var isAlive = step.Current is { IsDeleted: false } || (step.Current == null && step.Operation.Type == OperationType.Insert);
            if (!isAlive) continue;

            alive++;
            if (decision.Outcome == OperationOutcome.Rejected) rejected++;
            if (decision.Outcome == OperationOutcome.Merged) merged++;
            clientFields += step.Operation.Changes.Count;
            rejectedFields += decision.Outcome == OperationOutcome.Rejected
                ? step.Operation.Changes.Count
                : step.Operation.Changes.Count(c => !decision.FieldWrites.ContainsKey(c.Field));

            if (step.Operation.Type == OperationType.Delete && step.Current != null && IsRisky(registry, table, version, step))
            {
                risky++;
                if (!decision.Tombstone) unprotected++;
            }
        }

        var expected = CounterDeltas.ExpectedTotals(registry, input.InitialRecords, input.Events);
        var initial = CounterDeltas.ExpectedTotals(registry, input.InitialRecords, Array.Empty<OracleEvent>());
        long expectedIncrements = 0, lost = 0;
        foreach (var (cell, total) in expected)
        {
            var start = initial.TryGetValue(cell, out var v) ? v : 0L;
            expectedIncrements += Math.Abs(total - start);
            if (replay.Records.TryGetValue((cell.Table, cell.Id), out var record))
                lost += Math.Abs(total - AsLong(record.Values[cell.Field]));
        }

        return new StrategyMetrics
        {
            AliveOperations = alive,
            RejectedOperations = rejected,
            MergedOperations = merged,
            ClientFields = clientFields,
            RejectedFields = rejectedFields,
            UnknownFieldWrites = unknownWrites,
            ExpectedIncrements = expectedIncrements,
            LostIncrements = lost,
            RiskyDeletes = risky,
            RiskyDeletesWithoutTombstone = unprotected,
        };
    }

    private static bool IsRisky(SchemaRegistry registry, TableDefinition table, int version, ReplayStep step)
    {
        if (version >= registry.LatestVersion) return false;
        var current = step.Current!;
        var unknownData = table.Fields.Any(f =>
            !registry.IsKnown(version, table.Name, f.Name) &&
            current.Values.TryGetValue(f.Name, out var value) &&
            !FieldValues.AreEqual(f.Type, value, f.DefaultValue));
        var newerDependents = current.DependentTables.Any(t => registry.GetTable(t).IntroducedIn > version);
        return unknownData || newerDependents;
    }

    private static long AsLong(object? value) => value == null ? 0L : Convert.ToInt64(value);
}
