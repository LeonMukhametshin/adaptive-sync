using Sync.Core.Schema;
using Sync.Core.Strategies;
using Sync.Simulation;

namespace Sync.Strategies.Tests;

public class BenchmarkTests
{
    private static readonly Func<ISyncStrategy>[] Strategies =
    {
        () => new AdaptiveStrategy(),
        () => new ServerWinsStrategy(),
        () => new LwwStrategy(),
        () => new ClientWinsStrategy(),
        () => new FieldLwwNoSchemaStrategy(),
    };

    private static readonly Lazy<BenchmarkReport> Report =
        new(() => new Benchmark(SchemaVersions.Registry).Run(Strategies, Benchmark.DefaultGroups(seeds: 2)));

    [Fact]
    public void SameSeeds_GiveSameMetrics()
    {
        var again = new Benchmark(SchemaVersions.Registry).Run(Strategies, Benchmark.DefaultGroups(seeds: 2));

        Assert.Equal(Report.Value.Rows.Select(r => (r.Group, r.Metrics)), again.Rows.Select(r => (r.Group, r.Metrics)));
    }

    [Fact]
    public void Generator_IsDeterministic()
    {
        var generator = new ScenarioGenerator(SchemaVersions.Registry);
        var options = new GeneratorOptions { Seed = 7, OldClientShare = 0.5 };

        var first = generator.Generate(options).Input;
        var second = generator.Generate(options).Input;

        Assert.Equal(
            first.Events.Select(e => (e.Author, e.Table, e.RecordId, e.Operation, e.MadeAt, e.DeliveredAt, string.Join(",", e.Changes.Select(c => c.Field + "=" + c.NewValue)))),
            second.Events.Select(e => (e.Author, e.Table, e.RecordId, e.Operation, e.MadeAt, e.DeliveredAt, string.Join(",", e.Changes.Select(c => c.Field + "=" + c.NewValue)))));
    }

    [Fact]
    public void Adaptive_HasNoIncompatibilityLossAndMatchesOracle()
    {
        var adaptive = Total(AdaptiveStrategy.StrategyName);

        Assert.True(adaptive.IncompatibilityExposure > 0);
        Assert.Equal(0, adaptive.IncompatibilityLoss);
        Assert.Equal(0, adaptive.FinalMismatches);
        Assert.Equal(adaptive.ClientChanges, adaptive.ClientChangesCorrect);
        Assert.Equal(adaptive.Conflicts, adaptive.ConflictsCorrect);
        Assert.Equal(adaptive.ServerCells, adaptive.ServerCellsPreserved);
    }

    [Theory]
    [InlineData(ServerWinsStrategy.StrategyName)]
    [InlineData(LwwStrategy.StrategyName)]
    [InlineData(ClientWinsStrategy.StrategyName)]
    public void RecordLevelBaselines_LoseDataBecauseOfVersionIncompatibility(string strategy)
    {
        Assert.True(Total(strategy).IncompatibilityLoss > 0);
    }

    [Fact]
    public void Report_RendersEveryGroupAndStrategy()
    {
        var markdown = Benchmark.ToMarkdown(Report.Value);
        var csv = Benchmark.ToCsv(Report.Value).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("## " + Benchmark.TotalGroup, markdown);
        Assert.Contains("## " + Benchmark.CatalogGroup, markdown);
        Assert.Equal(Report.Value.Rows.Count + 1, csv.Length);
        foreach (var strategy in Report.Value.Strategies)
            Assert.Contains("| " + strategy + " |", markdown);
    }

    private static StrategyMetrics Total(string strategy) =>
        Report.Value.Group(Benchmark.TotalGroup).Single(r => r.Metrics.Strategy == strategy).Metrics;
}
