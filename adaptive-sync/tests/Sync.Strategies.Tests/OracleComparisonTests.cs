using Sync.Core.Schema;
using Sync.Core.Strategies;
using Sync.Oracle;
using Sync.Simulation;

namespace Sync.Strategies.Tests;

public class OracleComparisonTests
{
    private static readonly SchemaRegistry Registry = SchemaVersions.Registry;

    public static TheoryData<string> ScenarioNames()
    {
        var data = new TheoryData<string>();
        foreach (var scenario in OracleScenarios.All)
            data.Add(scenario.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void Adaptive_MatchesOracleStateOnEveryScenario(string name)
    {
        var (expected, actual) = Run(name, new AdaptiveStrategy());

        Assert.Empty(OracleComparison.Compare(Registry, expected, actual));
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void Adaptive_ClassifiesSituationsLikeOracle(string name)
    {
        var (expected, actual) = Run(name, new AdaptiveStrategy());

        Assert.Equal(
            expected.Classifications.Select(c => (c.EventIndex, c.Field, c.Situation)),
            OracleComparison.Classifications(actual));
    }

    [Theory]
    [InlineData(ServerWinsStrategy.StrategyName)]
    [InlineData(LwwStrategy.StrategyName)]
    [InlineData(ClientWinsStrategy.StrategyName)]
    [InlineData(FieldLwwNoSchemaStrategy.StrategyName)]
    public void Baselines_DivergeFromOracle(string strategy)
    {
        var mismatches = OracleScenarios.All
            .SelectMany(s => Compare(s, SyncStrategies.Create(strategy)))
            .ToList();

        Assert.NotEmpty(mismatches);
    }

    [Theory]
    [InlineData(LwwStrategy.StrategyName)]
    [InlineData(ClientWinsStrategy.StrategyName)]
    public void RecordLevelBaselines_LoseFieldsOutsideKnownSet(string strategy)
    {
        var scenario = OracleScenarios.All.Single(s => s.Name == "S2 old client, no conflict");

        var mismatches = Compare(scenario, SyncStrategies.Create(strategy));

        Assert.Contains(mismatches, m => m.Field == "region" && Equals(m.Expected, "eu") && m.Actual == null);
        Assert.Contains(mismatches, m => m.Field == "avatar_id" && Equals(m.Expected, "a0") && m.Actual == null);
    }

    [Fact]
    public void MixedScenario_AdaptiveKeepsEveryValueThatBaselinesLose()
    {
        var scenario = OracleScenarios.All.Single(s => s.Name == "Mixed v1, v2, v3 and server");
        var adaptive = Compare(scenario, new AdaptiveStrategy());
        var lost = SyncStrategies.Names
            .Where(n => n != AdaptiveStrategy.StrategyName)
            .ToDictionary(n => n, n => Compare(scenario, SyncStrategies.Create(n)).Count);

        Assert.Empty(adaptive);
        Assert.All(lost, pair => Assert.True(pair.Value > 0, pair.Key + " should diverge from the oracle"));
    }

    [Fact]
    public void Replay_AppliesTombstoneOnlyForAdaptive()
    {
        var scenario = OracleScenarios.All.Single(s => s.Name == "S5 delete with unknown data");

        var adaptive = new StrategyReplay(Registry).Run(scenario.Input, new AdaptiveStrategy());
        var serverWins = new StrategyReplay(Registry).Run(scenario.Input, new ServerWinsStrategy());

        Assert.True(adaptive.Record(OracleScenarios.Profile, OracleScenarios.P).IsTombstone);
        Assert.False(serverWins.Record(OracleScenarios.Profile, OracleScenarios.P).IsTombstone);
        Assert.Equal(SyncSituation.S5, adaptive.Steps.Single().Decision.Situation);
    }

    private static (OracleResult Expected, ReplayResult Actual) Run(string name, ISyncStrategy strategy)
    {
        var scenario = OracleScenarios.All.Single(s => s.Name == name);
        return (new SyncOracle(Registry).Run(scenario.Input), new StrategyReplay(Registry).Run(scenario.Input, strategy));
    }

    private static IReadOnlyList<StateMismatch> Compare(Scenario scenario, ISyncStrategy strategy) =>
        OracleComparison.Compare(
            Registry, new SyncOracle(Registry).Run(scenario.Input), new StrategyReplay(Registry).Run(scenario.Input, strategy));
}
