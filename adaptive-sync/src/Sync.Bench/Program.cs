using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Sync.Core.Schema;
using Sync.Core.Strategies;
using Sync.Simulation;
using Sync.Strategies;

namespace Sync.Bench;

public static class BenchProgram
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private static readonly Func<ISyncStrategy>[] Strategies =
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

    public static async Task Main(string[] args)
    {
        var output = args.Length > 0 ? args[0] : Path.Combine("..", "docs", "results");
        var summarySeeds = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 5;
        var statisticsSeeds = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 30;
        var mode = args.Length > 3 ? args[3] : "all";
        var endToEndSeeds = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 5;

        Console.OutputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(output);

        if (mode is "all" or "summary")
            RunSummary(output, summarySeeds);
        if (mode is "all" or "stats")
            RunStatistics(output, statisticsSeeds);
        if (mode is "e2e")
            await RunEndToEndAsync(output, endToEndSeeds);

        Console.WriteLine("Written to " + Path.GetFullPath(output));
    }

    private static void RunSummary(string output, int seeds)
    {
        var report = new Benchmark(SchemaVersions.Registry).Run(Strategies, Benchmark.DefaultGroups(seeds));
        File.WriteAllText(Path.Combine(output, "summary.md"), Benchmark.ToMarkdown(report), Utf8);
        File.WriteAllText(Path.Combine(output, "results.csv"), Benchmark.ToCsv(report), Utf8);
        Console.WriteLine("summary: " + report.Scenarios + " scenarios, " + report.Events + " events");
    }

    private static void RunStatistics(string output, int seeds)
    {
        var watch = Stopwatch.StartNew();
        var report = new Statistics(SchemaVersions.Registry).Run(Strategies, Ablation, Statistics.DefaultSeries(), seeds);
        var main = Strategies.Select(f => f().Name).ToList();

        var chartsDirectory = Path.Combine(output, "charts");
        Directory.CreateDirectory(chartsDirectory);
        var charts = new List<string>();
        foreach (var series in report.Series)
        {
            foreach (var metric in Statistics.Metrics.Where(m => m.Key is "loss" or "client" or "conflicts" or "lost-increments" or "rejected"))
            {
                var lines = main
                    .Select(name => new ChartSeries(name, series.Points.Select(p => (p.X, report.Get(series.Key, p.X, name, metric.Key))).ToList()))
                    .ToList();
                var file = series.Key + "-" + metric.Key + ".svg";
                File.WriteAllText(Path.Combine(chartsDirectory, file),
                    SvgLineChart.Render(metric.Title + " — " + series.Title.ToLowerInvariant(), series.Axis, metric.Title, metric.IsPercent, lines), Utf8);
                charts.Add("charts/" + file);
            }
        }

        File.WriteAllText(Path.Combine(output, "statistics.md"), Statistics.ToMarkdown(report, main, charts), Utf8);
        File.WriteAllText(Path.Combine(output, "statistics.csv"), Statistics.ToCsv(report), Utf8);
        Console.WriteLine("statistics: " + report.Seeds + " seeds per point, " + charts.Count + " charts, " +
                          watch.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
    }

    private static async Task RunEndToEndAsync(string output, int seeds)
    {
        var generator = new ScenarioGenerator(SchemaVersions.Registry);
        var scenarios = Enumerable.Range(1, seeds)
            .SelectMany(seed => new[]
            {
                generator.Generate(new GeneratorOptions { Seed = 500 + seed }),
                generator.Generate(new GeneratorOptions { Seed = 600 + seed, Clients = 20, Profiles = 20, OfflineOperations = 6 }),
            })
            .ToList();
        var admin = Environment.GetEnvironmentVariable("BENCH_PG")
                    ?? "Host=localhost;Port=55432;Database=adaptive_sync;Username=sync;Password=sync_dev";

        var benchmark = new EndToEndBenchmark(Directory.GetCurrentDirectory(), admin);
        var results = await benchmark.RunAsync(EndToEndBenchmark.DefaultModes, scenarios, generator.Generate(new GeneratorOptions { Seed = 499 }));

        var environment = string.Format(CultureInfo.InvariantCulture,
            "Сценариев на режим: {0} ({1} в базовой конфигурации и {1} с 20 клиентами, 20 профилями и офлайном по 6 операций), " +
            "перед замером — прогревочный прогон. Клиент — HTTP-запросы `/sync` по loopback к серверу на Kestrel, PostgreSQL 16 в Docker. " +
            "Машина: {2}, логических процессоров: {3}, {4}. Время на стороне сервера и метода — из заголовка `Server-Timing`.",
            scenarios.Count, seeds, RuntimeInformation.OSDescription, Environment.ProcessorCount, RuntimeInformation.FrameworkDescription);
        var markdown = EndToEndBenchmark.ToMarkdown(results, environment);
        File.WriteAllText(Path.Combine(output, "end-to-end.md"), markdown, Utf8);
        File.WriteAllText(Path.Combine(output, "end-to-end.csv"), EndToEndBenchmark.ToCsv(results), Utf8);
        Console.WriteLine(markdown);
    }
}
