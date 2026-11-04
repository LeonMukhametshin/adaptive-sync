using Sync.Core.Schema;
using Sync.Oracle;
using Sync.Simulation;

namespace Sync.Strategies.Tests;

public class GeneratedScenarioTests
{
    private static readonly SchemaRegistry Registry = SchemaVersions.Registry;

    [Theory]
    [InlineData(1, 0.5, 3, 0.3)]
    [InlineData(2, 0.75, 6, 0.5)]
    [InlineData(3, 0.25, 6, 0.1)]
    [InlineData(4, 0.5, 1, 0.5)]
    public void Adaptive_MatchesOracleOnGeneratedScenarios(int seed, double oldShare, int offline, double conflict)
    {
        var scenario = new ScenarioGenerator(Registry).Generate(new GeneratorOptions
        {
            Seed = seed, OldClientShare = oldShare, OfflineOperations = offline, ConflictRate = conflict,
        });

        var expected = new SyncOracle(Registry).Run(scenario.Input);
        var actual = new StrategyReplay(Registry).Run(scenario.Input, new AdaptiveStrategy());
        var mismatches = OracleComparison.Compare(Registry, expected, actual);

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Select(m => Describe(scenario, m))));
    }

    public static TheoryData<string, int> BenchmarkScenarios()
    {
        var data = new TheoryData<string, int>();
        foreach (var group in Benchmark.DefaultGroups(seeds: 2))
        {
            for (var i = 0; i < group.Scenarios.Count; i++)
                data.Add(group.Name, i);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(BenchmarkScenarios))]
    public void Adaptive_MatchesOracleOnBenchmarkScenarios(string group, int index)
    {
        var scenario = Benchmark.DefaultGroups(seeds: 2).Single(g => g.Name == group).Scenarios[index];

        var expected = new SyncOracle(Registry).Run(scenario.Input);
        var actual = new StrategyReplay(Registry).Run(scenario.Input, new AdaptiveStrategy());
        var mismatches = OracleComparison.Compare(Registry, expected, actual);

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Select(m => Describe(scenario, m))));
    }

    private static string Describe(Scenario scenario, StateMismatch m)
    {
        var events = scenario.Input.Events
            .Select((e, i) => (e, i))
            .Where(p => p.e.Table == m.Table && p.e.RecordId == m.RecordId &&
                        (p.e.Changes.Count == 0 || p.e.Changes.Any(c => c.Field == m.Field)))
            .Select(p => $"    #{p.i} {p.e.Author} v{p.e.SchemaVersion} {p.e.Operation} seen={p.e.SeenAt} made={p.e.MadeAt} delivered={p.e.DeliveredAt} " +
                         string.Join(", ", p.e.Changes.Where(c => c.Field == m.Field).Select(c => $"{c.OldValue}->{c.NewValue}")));
        return $"{m.Table}.{m.Field} expected={m.Expected} actual={m.Actual}\n" + string.Join("\n", events);
    }
}
