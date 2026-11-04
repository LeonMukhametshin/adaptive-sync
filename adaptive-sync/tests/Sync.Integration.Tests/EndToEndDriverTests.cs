using Sync.Bench;
using Sync.Core.Schema;
using Sync.Simulation;
using Sync.Strategies;

namespace Sync.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class EndToEndDriverTests(PostgresFixture postgres)
{
    [SkippableTheory]
    [InlineData(AdaptiveStrategy.StrategyName)]
    [InlineData(ServerWinsStrategy.StrategyName)]
    [InlineData(LwwStrategy.StrategyName)]
    [InlineData(ClientWinsStrategy.StrategyName)]
    [InlineData(FieldLwwNoSchemaStrategy.StrategyName)]
    public async Task GeneratedJournal_ServerStateMatchesInMemoryModel(string strategy)
    {
        await using var host = await SyncTestHost.StartAsync(postgres, strategy);
        var scenario = new ScenarioGenerator(SchemaVersions.Registry).Generate(new GeneratorOptions
        {
            Seed = 3, Clients = 4, Rounds = 3, ConflictRate = 0.5, OldClientShare = 0.5,
        });
        var replay = new StrategyReplay(SchemaVersions.Registry).Run(scenario.Input, SyncStrategies.Create(strategy));

        var run = await new EndToEndDriver(host.CreateHttpClient(), host.ConnectionString, SchemaVersions.Registry)
            .RunAsync(scenario.Input, replay);

        Assert.Empty(run.Mismatches);
        var pushes = run.Requests.Where(r => r.Kind == EndToEndDriver.Push).ToList();
        Assert.Equal(replay.Steps.Count, pushes.Sum(r => r.Operations));
        Assert.Equal(replay.Steps.Count, pushes.Sum(r => r.Decisions));
        Assert.All(run.Requests, r => Assert.True(r.ServerMs > 0 && r.RequestBytes > 0 && r.ResponseBytes > 0));
        Assert.Contains(run.Requests, r => r.Kind == EndToEndDriver.Pull);
    }

    [SkippableFact]
    public async Task CatalogJournals_ServerStateMatchesModelOnAdaptive()
    {
        await using var host = await SyncTestHost.StartAsync(postgres, AdaptiveStrategy.StrategyName);
        var driver = new EndToEndDriver(host.CreateHttpClient(), host.ConnectionString, SchemaVersions.Registry);

        foreach (var scenario in OracleScenarios.All)
        {
            var replay = new StrategyReplay(SchemaVersions.Registry).Run(scenario.Input, new AdaptiveStrategy());
            var run = await driver.RunAsync(scenario.Input, replay);
            Assert.True(run.Mismatches.Count == 0, scenario.Name + ": " + string.Join("; ", run.Mismatches));
        }
    }

    [Fact]
    public void Percentile_InterpolatesBetweenRanks()
    {
        Assert.Equal(2.5, EndToEndBenchmark.Percentile(new double[] { 1, 2, 3, 4 }, 50), 9);
        Assert.Equal(3.85, EndToEndBenchmark.Percentile(new double[] { 1, 2, 3, 4 }, 95), 9);
    }
}
