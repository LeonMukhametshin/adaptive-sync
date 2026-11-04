using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Strategies;
using Sync.Oracle;

namespace Sync.Simulation;

public sealed record StrategyMetrics
{
    public string Strategy { get; init; } = string.Empty;

    public int Operations { get; init; }

    public int ClientChanges { get; init; }

    public int ClientChangesCorrect { get; init; }

    public int Conflicts { get; init; }

    public int ConflictsCorrect { get; init; }

    public int UnknownCells { get; init; }

    public int UnknownCellsLost { get; init; }

    public int Tombstones { get; init; }

    public int TombstonesLost { get; init; }

    public int ServerCells { get; init; }

    public int ServerCellsPreserved { get; init; }

    public int FinalMismatches { get; init; }

    public int AliveOperations { get; init; }

    public int RejectedOperations { get; init; }

    public int MergedOperations { get; init; }

    public int ClientFields { get; init; }

    public int RejectedFields { get; init; }

    public int UnknownFieldWrites { get; init; }

    public long ExpectedIncrements { get; init; }

    public long LostIncrements { get; init; }

    public int RiskyDeletes { get; init; }

    public int RiskyDeletesWithoutTombstone { get; init; }

    public int IncompatibilityLoss => UnknownCellsLost + TombstonesLost;

    public int IncompatibilityExposure => UnknownCells + Tombstones;

    public static StrategyMetrics Sum(string strategy, IEnumerable<StrategyMetrics> items)
    {
        var list = items.ToList();
        return new StrategyMetrics
        {
            Strategy = strategy,
            Operations = list.Sum(m => m.Operations),
            ClientChanges = list.Sum(m => m.ClientChanges),
            ClientChangesCorrect = list.Sum(m => m.ClientChangesCorrect),
            Conflicts = list.Sum(m => m.Conflicts),
            ConflictsCorrect = list.Sum(m => m.ConflictsCorrect),
            UnknownCells = list.Sum(m => m.UnknownCells),
            UnknownCellsLost = list.Sum(m => m.UnknownCellsLost),
            Tombstones = list.Sum(m => m.Tombstones),
            TombstonesLost = list.Sum(m => m.TombstonesLost),
            ServerCells = list.Sum(m => m.ServerCells),
            ServerCellsPreserved = list.Sum(m => m.ServerCellsPreserved),
            FinalMismatches = list.Sum(m => m.FinalMismatches),
            AliveOperations = list.Sum(m => m.AliveOperations),
            RejectedOperations = list.Sum(m => m.RejectedOperations),
            MergedOperations = list.Sum(m => m.MergedOperations),
            ClientFields = list.Sum(m => m.ClientFields),
            RejectedFields = list.Sum(m => m.RejectedFields),
            UnknownFieldWrites = list.Sum(m => m.UnknownFieldWrites),
            ExpectedIncrements = list.Sum(m => m.ExpectedIncrements),
            LostIncrements = list.Sum(m => m.LostIncrements),
            RiskyDeletes = list.Sum(m => m.RiskyDeletes),
            RiskyDeletesWithoutTombstone = list.Sum(m => m.RiskyDeletesWithoutTombstone),
        };
    }
}

public sealed class OracleTrace
{
    private OracleTrace(OracleResult final, IReadOnlyDictionary<int, ExpectedRecord> afterEvent)
    {
        Final = final;
        AfterEvent = afterEvent;
    }

    public OracleResult Final { get; }

    public IReadOnlyDictionary<int, ExpectedRecord> AfterEvent { get; }

    public static OracleTrace Build(SyncOracle oracle, OracleInput input)
    {
        var afterEvent = new Dictionary<int, ExpectedRecord>();
        var final = oracle.Run(input, (index, record) => afterEvent[index] = record);
        return new OracleTrace(final, afterEvent);
    }
}

public static class MetricsCalculator
{
    public static StrategyMetrics Calculate(
        SchemaRegistry registry, string strategy, OracleInput input, OracleTrace trace, ReplayResult replay)
    {
        var steps = replay.Steps.ToDictionary(s => s.EventIndex);
        int clientChanges = 0, clientCorrect = 0, conflicts = 0, conflictsCorrect = 0, tombstones = 0, tombstonesLost = 0;

        foreach (var c in trace.Final.Classifications)
        {
            var expected = trace.AfterEvent.TryGetValue(c.EventIndex, out var record) ? record : null;
            var after = steps.TryGetValue(c.EventIndex, out var step) ? step.After : null;
            var table = registry.GetTable(c.Table);

            if (c.Field == null)
            {
                if (c.Situation != SyncSituation.S5) continue;
                tombstones++;
                if (expected == null || after == null || !after.IsTombstone || !after.IsDeleted)
                    tombstonesLost++;
                continue;
            }

            var correct = expected != null && after != null &&
                          FieldValues.AreEqual(table.FindField(c.Field)!.Type, expected.Values[c.Field], after.Values[c.Field]);
            if (c.Situation == SyncSituation.S4)
            {
                conflicts++;
                if (correct) conflictsCorrect++;
            }
            else
            {
                clientChanges++;
                if (correct) clientCorrect++;
            }
        }

        var mismatches = OracleComparison.Compare(registry, trace.Final, replay);
        var mismatched = mismatches.Select(m => (m.Table, m.RecordId, m.Field)).ToHashSet();

        var unknown = ProtectedCells(registry, input);
        var server = ServerCells(registry, input).Where(c => !unknown.Contains(c)).ToList();

        var direct = DirectMetrics.Calculate(registry, input, replay);
        return direct with
        {
            Strategy = strategy,
            Operations = replay.Steps.Count,
            ClientChanges = clientChanges,
            ClientChangesCorrect = clientCorrect,
            Conflicts = conflicts,
            ConflictsCorrect = conflictsCorrect,
            UnknownCells = unknown.Count,
            UnknownCellsLost = unknown.Count(mismatched.Contains),
            Tombstones = tombstones,
            TombstonesLost = tombstonesLost,
            ServerCells = server.Count,
            ServerCellsPreserved = server.Count(c => !mismatched.Contains(c)),
            FinalMismatches = mismatches.Count,
        };
    }

    private static HashSet<(string Table, Guid Id, string Field)> ProtectedCells(SchemaRegistry registry, OracleInput input)
    {
        var cells = new HashSet<(string, Guid, string)>();
        foreach (var e in input.Events.Where(e => !e.IsServer && e.SchemaVersion < registry.LatestVersion))
        {
            foreach (var field in registry.GetTable(e.Table).Fields.Where(f => !registry.IsKnown(e.SchemaVersion, e.Table, f.Name)))
                cells.Add((e.Table, e.RecordId, field.Name));
        }
        return cells;
    }

    private static IEnumerable<(string Table, Guid Id, string Field)> ServerCells(SchemaRegistry registry, OracleInput input)
    {
        var lastWriter = new Dictionary<(string, Guid, string), bool>();
        foreach (var record in input.InitialRecords)
        {
            foreach (var field in registry.GetTable(record.Table).Fields)
                lastWriter[(record.Table, record.RecordId, field.Name)] = true;
        }
        foreach (var e in input.Events)
        {
            foreach (var change in e.Changes)
                lastWriter[(e.Table, e.RecordId, change.Field)] = e.IsServer;
        }
        return lastWriter.Where(p => p.Value).Select(p => p.Key);
    }
}
