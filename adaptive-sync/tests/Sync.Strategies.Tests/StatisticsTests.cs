using System.Xml.Linq;
using Sync.Core.Schema;
using Sync.Core.Strategies;
using Sync.Oracle;
using Sync.Simulation;

namespace Sync.Strategies.Tests;

public class StatisticsTests
{
    private static readonly SchemaRegistry Registry = SchemaVersions.Registry;

    private static readonly Func<ISyncStrategy>[] Main =
    {
        () => new AdaptiveStrategy(),
        () => new ServerWinsStrategy(),
        () => new LwwStrategy(),
        () => new ClientWinsStrategy(),
        () => new FieldLwwNoSchemaStrategy(),
    };

    private static readonly Func<ISyncStrategy>[] Ablation =
    {
        AdaptiveStrategy.WithoutSchemaVersion,
        AdaptiveStrategy.WithoutCategories,
        AdaptiveStrategy.WithRecordLevelConflicts,
    };

    private static readonly Lazy<StatisticsReport> Baseline =
        new(() => new Statistics(Registry).Run(Main, Ablation, Array.Empty<SeriesDefinition>(), seeds: 8));

    [Theory]
    [InlineData(AdaptiveStrategy.StrategyName, 0)]
    [InlineData(ServerWinsStrategy.StrategyName, 0)]
    [InlineData(LwwStrategy.StrategyName, 3)]
    [InlineData(ClientWinsStrategy.StrategyName, 3)]
    [InlineData(FieldLwwNoSchemaStrategy.StrategyName, 0)]
    public void UnknownFieldWrites_CountDefaultsWrittenOverFieldsOutsideKnownSet(string strategy, int expected)
    {
        Assert.Equal(expected, Direct("S2 old client, no conflict", strategy).UnknownFieldWrites);
    }

    [Theory]
    [InlineData(AdaptiveStrategy.StrategyName, 0)]
    [InlineData(ServerWinsStrategy.StrategyName, 10)]
    [InlineData(LwwStrategy.StrategyName, 10)]
    [InlineData(ClientWinsStrategy.StrategyName, 30)]
    [InlineData(FieldLwwNoSchemaStrategy.StrategyName, 10)]
    public void LostIncrements_CompareFinalCounterWithSumOfAllIncrements(string strategy, long expected)
    {
        var metrics = Direct("S4 counter", strategy);

        Assert.Equal(40, metrics.ExpectedIncrements);
        Assert.Equal(expected, metrics.LostIncrements);
    }

    [Theory]
    [InlineData(AdaptiveStrategy.StrategyName, 0)]
    [InlineData(ServerWinsStrategy.StrategyName, 1)]
    [InlineData(ClientWinsStrategy.StrategyName, 1)]
    public void RiskyDeletes_CountDeletesOfRecordsWithNewerDataWithoutTombstone(string strategy, int unprotected)
    {
        foreach (var name in new[] { "S5 delete with unknown data", "S5 delete with dependents in newer table" })
        {
            var metrics = Direct(name, strategy);
            Assert.Equal(1, metrics.RiskyDeletes);
            Assert.Equal(unprotected, metrics.RiskyDeletesWithoutTombstone);
        }
        Assert.Equal(0, Direct("Old client delete without unknown data", strategy).RiskyDeletes);
    }

    [Fact]
    public void RejectedOperations_CountOnlyOperationsOnLiveRecords()
    {
        var serverAuthoritative = Direct("S4 server_authoritative", AdaptiveStrategy.StrategyName);
        var deletedRecord = Direct("Insert by old client, then server delete", AdaptiveStrategy.StrategyName);

        Assert.Equal((1, 1, 1, 1), (serverAuthoritative.AliveOperations, serverAuthoritative.RejectedOperations,
            serverAuthoritative.ClientFields, serverAuthoritative.RejectedFields));
        Assert.Equal((1, 0), (deletedRecord.AliveOperations, deletedRecord.RejectedOperations));
    }

    [Fact]
    public void GeneratedCounters_AreConsistentWithOracle()
    {
        var scenario = new ScenarioGenerator(Registry).Generate(new GeneratorOptions { Seed = 5, ConflictRate = 0.5, OfflineOperations = 6 });

        var expected = CounterDeltas.ExpectedTotals(Registry, scenario.Input.InitialRecords, scenario.Input.Events);
        var oracle = new SyncOracle(Registry).Run(scenario.Input);

        Assert.NotEmpty(expected);
        foreach (var ((table, id, field), total) in expected)
            Assert.Equal(total, oracle.Value(table, id, field));
    }

    [Fact]
    public void FullMethod_HasNoDirectLosses()
    {
        foreach (var metric in new[] { "loss", "unknown-writes", "lost-increments", "unprotected-deletes" })
            Assert.Equal(0, Baseline.Value.GetBaseline(AdaptiveStrategy.StrategyName, metric).Mean, 6);
        Assert.Equal(100, Baseline.Value.GetBaseline(AdaptiveStrategy.StrategyName, "conflicts").Mean, 6);
    }

    [Fact]
    public void EveryAblationVariant_IsWorseThanFullMethodInSomeMetric()
    {
        var report = Baseline.Value;
        var lowerIsBetter = new[] { "loss", "unknown-writes", "lost-increments", "unprotected-deletes", "rejected-fields" };
        var higherIsBetter = new[] { "client", "server", "conflicts" };

        foreach (var variant in Ablation.Select(f => f().Name))
        {
            var worse = lowerIsBetter.Where(m => report.GetBaseline(variant, m).Mean > report.GetBaseline(AdaptiveStrategy.StrategyName, m).Mean + 1e-9)
                .Concat(higherIsBetter.Where(m => report.GetBaseline(variant, m).Mean < report.GetBaseline(AdaptiveStrategy.StrategyName, m).Mean - 1e-9))
                .ToList();
            Assert.True(worse.Count > 0, variant + " is not worse than the full method in any metric");
        }
    }

    [Fact]
    public void Run_IsDeterministic()
    {
        var series = new[] { Statistics.DefaultSeries()[0] with { Points = Statistics.DefaultSeries()[0].Points.Take(2).ToList() } };

        var first = Statistics.ToCsv(new Statistics(Registry).Run(Main, Ablation, series, seeds: 2));
        var second = Statistics.ToCsv(new Statistics(Registry).Run(Main, Ablation, series, seeds: 2));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Estimate_UsesStudentInterval()
    {
        var thirty = Estimate.From(Enumerable.Range(1, 30).Select(i => (double)i).ToList());
        var five = Estimate.From(new double[] { 1, 2, 3, 4, 5 });
        var empty = Estimate.From(new[] { double.NaN });

        Assert.Equal(15.5, thirty.Mean, 9);
        Assert.Equal(8.8034, thirty.StdDev, 3);
        Assert.Equal(2.0452 * 8.8034 / Math.Sqrt(30), thirty.Ci95, 2);
        Assert.Equal(2.776 * 1.5811 / Math.Sqrt(5), five.Ci95, 1);
        Assert.Equal(0, empty.N);
    }

    [Fact]
    public void Chart_IsValidSvgWithLegendAndPointTitles()
    {
        var svg = SvgLineChart.Render("Потеря", "x", "%", true, new[]
        {
            new ChartSeries("Adaptive", new[] { (0.0, new Estimate(3, 0, 0, 0)), (10.0, new Estimate(3, 0, 0, 0)) }),
            new ChartSeries("Lww", new[] { (0.0, new Estimate(3, 10, 2, 3)), (10.0, new Estimate(3, 40, 5, 6)) }),
        });

        var document = XDocument.Parse(svg);
        XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.Equal(4, document.Descendants(ns + "title").Count());
        Assert.Contains(document.Descendants(ns + "text"), t => t.Value == "Adaptive");
        Assert.Contains(document.Descendants(ns + "text"), t => t.Value == "Lww");
    }

    private static StrategyMetrics Direct(string scenario, string strategy)
    {
        var input = OracleScenarios.All.Single(s => s.Name == scenario).Input;
        return DirectMetrics.Calculate(Registry, input, new StrategyReplay(Registry).Run(input, SyncStrategies.Create(strategy)));
    }
}
