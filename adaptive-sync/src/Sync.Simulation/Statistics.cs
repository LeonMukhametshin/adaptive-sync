using System.Globalization;
using System.Text;
using Sync.Core.Schema;
using Sync.Core.Strategies;
using Sync.Oracle;

namespace Sync.Simulation;

public sealed record MetricDefinition(string Key, string Title, bool IsPercent, Func<StrategyMetrics, double> Value);

public sealed record SeriesPoint(double X, Func<int, GeneratorOptions> Options);

public sealed record SeriesDefinition(string Key, string Title, string Axis, IReadOnlyList<SeriesPoint> Points);

public sealed record Estimate(int N, double Mean, double StdDev, double Ci95)
{
    public static Estimate From(IReadOnlyList<double> values)
    {
        var finite = values.Where(v => !double.IsNaN(v)).ToList();
        if (finite.Count == 0) return new Estimate(0, double.NaN, double.NaN, double.NaN);
        var mean = finite.Average();
        if (finite.Count == 1) return new Estimate(1, mean, 0, 0);
        var sd = Math.Sqrt(finite.Sum(v => (v - mean) * (v - mean)) / (finite.Count - 1));
        return new Estimate(finite.Count, mean, sd, StudentT975(finite.Count - 1) * sd / Math.Sqrt(finite.Count));
    }

    private static readonly double[] StudentTable =
    {
        12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228, 2.201, 2.179, 2.160, 2.145, 2.131,
        2.120, 2.110, 2.101, 2.093, 2.086, 2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
    };

    private static double StudentT975(int df)
    {
        if (df <= StudentTable.Length) return StudentTable[df - 1];
        const double z = 1.959963984540054;
        var z3 = z * z * z;
        var z5 = z3 * z * z;
        return z + (z3 + z) / (4.0 * df) + (5 * z5 + 16 * z3 + 3 * z) / (96.0 * df * df);
    }
}

public sealed record StatisticsCell(string Series, double X, string Strategy, string Metric, Estimate Estimate);

public sealed class StatisticsReport
{
    public StatisticsReport(
        IReadOnlyList<string> strategies, IReadOnlyList<SeriesDefinition> series, IReadOnlyList<StatisticsCell> cells,
        IReadOnlyList<StatisticsCell> baseline, int seeds)
    {
        Strategies = strategies;
        Series = series;
        Cells = cells;
        Baseline = baseline;
        Seeds = seeds;
    }

    public IReadOnlyList<string> Strategies { get; }

    public IReadOnlyList<SeriesDefinition> Series { get; }

    public IReadOnlyList<StatisticsCell> Cells { get; }

    public IReadOnlyList<StatisticsCell> Baseline { get; }

    public int Seeds { get; }

    public Estimate Get(string series, double x, string strategy, string metric) =>
        Cells.Single(c => c.Series == series && c.X == x && c.Strategy == strategy && c.Metric == metric).Estimate;

    public Estimate GetBaseline(string strategy, string metric) =>
        Baseline.Single(c => c.Strategy == strategy && c.Metric == metric).Estimate;
}

public sealed class Statistics(SchemaRegistry registry)
{
    public static readonly IReadOnlyList<MetricDefinition> Metrics = new MetricDefinition[]
    {
        new("loss", "Потеря из-за несовместимости версий, %", true, m => Rate(m.IncompatibilityLoss, m.IncompatibilityExposure)),
        new("client", "Корректно применённые изменения клиентов, %", true, m => Rate(m.ClientChangesCorrect, m.ClientChanges)),
        new("server", "Сохранённые данные сервера, %", true, m => Rate(m.ServerCellsPreserved, m.ServerCells)),
        new("conflicts", "Конфликты (S4), решённые верно, %", true, m => Rate(m.ConflictsCorrect, m.Conflicts)),
        new("unknown-writes", "Записи в поля вне K(v) на 100 операций", false, m => Rate(m.UnknownFieldWrites, m.AliveOperations)),
        new("lost-increments", "Потерянные приращения счётчиков, %", true, m => Rate(m.LostIncrements, m.ExpectedIncrements)),
        new("rejected", "Отклонённые операции клиентов, %", true, m => Rate(m.RejectedOperations, m.AliveOperations)),
        new("rejected-fields", "Отклонённые изменения полей, %", true, m => Rate(m.RejectedFields, m.ClientFields)),
        new("unprotected-deletes", "Удаления записей с данными новых версий без tombstone, %", true,
            m => Rate(m.RiskyDeletesWithoutTombstone, m.RiskyDeletes)),
        new("merged", "Операции с исходом Merged, %", true, m => Rate(m.MergedOperations, m.AliveOperations)),
    };

    public static IReadOnlyList<SeriesDefinition> DefaultSeries() => new SeriesDefinition[]
    {
        new("old-share", "Доля клиентов старых версий", "Доля клиентов v1/v2, %",
            Enumerable.Range(0, 11).Select(i => Point(i * 10, s => new GeneratorOptions { Seed = s, OldClientShare = i / 10.0 })).ToList()),
        new("offline", "Длина офлайна", "Операций за сеанс офлайн",
            Enumerable.Range(1, 10).Select(n => Point(n, s => new GeneratorOptions { Seed = s, OfflineOperations = n })).ToList()),
        new("conflict", "Конфликтность", "Доля операций над «горячей» записью, %",
            Enumerable.Range(0, 7).Select(i => Point(i * 10, s => new GeneratorOptions { Seed = s, ConflictRate = i / 10.0 })).ToList()),
        new("clients", "Число клиентов", "Клиентов",
            new[] { 2, 4, 6, 8, 12, 16, 20 }.Select(n => Point(n, s => new GeneratorOptions { Seed = s, Clients = n })).ToList()),
        new("records", "Число записей", "Профилей (по 2 предмета на профиль)",
            new[] { 2, 5, 10, 20, 35, 50 }.Select(n => Point(n, s => new GeneratorOptions { Seed = s, Profiles = n })).ToList()),
    };

    public StatisticsReport Run(
        IReadOnlyList<Func<ISyncStrategy>> strategies, IReadOnlyList<Func<ISyncStrategy>> ablation,
        IReadOnlyList<SeriesDefinition> series, int seeds)
    {
        var generator = new ScenarioGenerator(registry);
        var names = strategies.Select(f => f().Name).ToList();

        var jobs = new List<(string Series, double X, Scenario Scenario)>();
        for (var si = 0; si < series.Count; si++)
        {
            for (var pi = 0; pi < series[si].Points.Count; pi++)
            {
                var point = series[si].Points[pi];
                for (var seed = 1; seed <= seeds; seed++)
                    jobs.Add((series[si].Key, point.X, generator.Generate(point.Options(SeedFor(si, pi, seed)))));
            }
        }

        var results = Evaluate(jobs.Select(j => j.Scenario).ToList(), strategies);
        var cells = new List<StatisticsCell>();
        foreach (var group in jobs.Select((j, i) => (j, i)).GroupBy(p => (p.j.Series, p.j.X)))
        {
            foreach (var name in names)
            {
                var perSeed = group.Select(p => results[p.i][name]).ToList();
                foreach (var metric in Metrics)
                    cells.Add(new StatisticsCell(group.Key.Series, group.Key.X, name, metric.Key, Estimate.From(perSeed.Select(metric.Value).ToList())));
            }
        }

        var all = strategies.Concat(ablation).ToList();
        var baselineScenarios = Enumerable.Range(1, seeds)
            .Select(seed => generator.Generate(new GeneratorOptions { Seed = 900_000 + seed }))
            .ToList();
        var baselineResults = Evaluate(baselineScenarios, all);
        var baseline = new List<StatisticsCell>();
        foreach (var name in all.Select(f => f().Name))
        {
            foreach (var metric in Metrics)
            {
                baseline.Add(new StatisticsCell("baseline", 0, name, metric.Key,
                    Estimate.From(baselineResults.Select(r => metric.Value(r[name])).ToList())));
            }
        }

        return new StatisticsReport(all.Select(f => f().Name).ToList(), series, cells, baseline, seeds);
    }

    public static string ToCsv(StatisticsReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("series,x,strategy,metric,n,mean,std_dev,ci95");
        foreach (var c in report.Baseline.Concat(report.Cells))
        {
            sb.AppendLine(string.Join(",", c.Series, F(c.X), c.Strategy, c.Metric, c.Estimate.N.ToString(CultureInfo.InvariantCulture),
                F(c.Estimate.Mean), F(c.Estimate.StdDev), F(c.Estimate.Ci95)));
        }
        return sb.ToString();
    }

    public static string ToMarkdown(StatisticsReport report, IReadOnlyList<string> mainStrategies, IReadOnlyList<string> chartFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Статистика по сериям экспериментов");
        sb.AppendLine();
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "{0} seed на каждую точку. Значение — среднее по seed ± половина 95 % доверительного интервала (t-распределение). " +
            "Базовая конфигурация: 4 профиля, 6 клиентов, 50 % старых, 4 сеанса по 3 операции, конфликтность 30 %. " +
            "Определения метрик — `docs/method.md`, разделы «Мини-бенчмарк» и «Метрики без оракула».", report.Seeds));
        sb.AppendLine();
        sb.AppendLine("## Базовая конфигурация и варианты метода (ablation)");
        sb.AppendLine();
        sb.AppendLine("| Метрика | " + string.Join(" | ", report.Strategies) + " |");
        sb.AppendLine("| --- |" + string.Concat(report.Strategies.Select(_ => " --- |")));
        foreach (var metric in Metrics)
            sb.AppendLine("| " + metric.Title + " | " + string.Join(" | ", report.Strategies.Select(s => Format(report.GetBaseline(s, metric.Key), metric))) + " |");
        sb.AppendLine();

        foreach (var series in report.Series)
        {
            sb.AppendLine("## " + series.Title);
            sb.AppendLine();
            foreach (var metric in Metrics.Where(m => m.Key is "loss" or "client" or "conflicts" or "lost-increments" or "rejected"))
            {
                sb.AppendLine("**" + metric.Title + "**");
                sb.AppendLine();
                sb.AppendLine("| " + series.Axis + " | " + string.Join(" | ", mainStrategies) + " |");
                sb.AppendLine("| --- |" + string.Concat(mainStrategies.Select(_ => " --- |")));
                foreach (var point in series.Points)
                {
                    sb.AppendLine("| " + F(point.X) + " | " +
                                  string.Join(" | ", mainStrategies.Select(s => Format(report.Get(series.Key, point.X, s, metric.Key), metric))) + " |");
                }
                sb.AppendLine();
            }
            foreach (var chart in chartFiles.Where(f => Path.GetFileName(f).StartsWith(series.Key + "-", StringComparison.Ordinal)))
                sb.AppendLine("![" + series.Title + "](" + chart.Replace('\\', '/') + ")");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private List<Dictionary<string, StrategyMetrics>> Evaluate(IReadOnlyList<Scenario> scenarios, IReadOnlyList<Func<ISyncStrategy>> strategies)
    {
        var results = new Dictionary<string, StrategyMetrics>[scenarios.Count];
        Parallel.For(0, scenarios.Count, i =>
        {
            var oracle = new SyncOracle(registry);
            var replay = new StrategyReplay(registry);
            var input = scenarios[i].Input;
            var trace = OracleTrace.Build(oracle, input);
            var row = new Dictionary<string, StrategyMetrics>();
            foreach (var factory in strategies)
            {
                var strategy = factory();
                row[strategy.Name] = MetricsCalculator.Calculate(registry, strategy.Name, input, trace, replay.Run(input, strategy));
            }
            results[i] = row;
        });
        return results.ToList();
    }

    private static SeriesPoint Point(double x, Func<int, GeneratorOptions> options) => new(x, options);

    private static int SeedFor(int series, int point, int seed) => (series + 1) * 100_000 + (point + 1) * 1_000 + seed;

    private static double Rate(double part, double total) => total == 0 ? double.NaN : 100.0 * part / total;

    private static string Format(Estimate estimate, MetricDefinition metric) =>
        estimate.N == 0 ? "—" : string.Format(CultureInfo.InvariantCulture, "{0:0.0} ± {1:0.0}", estimate.Mean, estimate.Ci95);

    private static string F(double value) =>
        double.IsNaN(value) ? "" : value.ToString("0.####", CultureInfo.InvariantCulture);
}
