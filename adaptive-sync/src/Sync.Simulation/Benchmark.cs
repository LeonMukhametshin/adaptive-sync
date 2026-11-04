using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Strategies;
using Sync.Oracle;

namespace Sync.Simulation;

public sealed record BenchmarkGroup(string Name, IReadOnlyList<Scenario> Scenarios);

public sealed record BenchmarkRow(string Group, StrategyMetrics Metrics, double MicrosecondsPerOperation);

public sealed class BenchmarkReport
{
    public BenchmarkReport(IReadOnlyList<string> strategies, IReadOnlyList<BenchmarkRow> rows, int scenarios, int events)
    {
        Strategies = strategies;
        Rows = rows;
        Scenarios = scenarios;
        Events = events;
    }

    public IReadOnlyList<string> Strategies { get; }

    public IReadOnlyList<BenchmarkRow> Rows { get; }

    public int Scenarios { get; }

    public int Events { get; }

    public IEnumerable<BenchmarkRow> Group(string name) => Rows.Where(r => r.Group == name);
}

public sealed class Benchmark(SchemaRegistry registry)
{
    public const string TotalGroup = "Все сценарии";
    public const string CatalogGroup = "Ручные сценарии оракула";

    public static IReadOnlyList<BenchmarkGroup> DefaultGroups(int seeds = 5)
    {
        var generator = new ScenarioGenerator(SchemaVersions.Registry);
        var groups = new List<BenchmarkGroup> { new(CatalogGroup, OracleScenarios.All) };
        foreach (var share in new[] { 0.0, 0.25, 0.5, 0.75 })
        {
            groups.Add(new BenchmarkGroup(
                string.Format(CultureInfo.InvariantCulture, "Доля старых клиентов {0:0}%", share * 100),
                Enumerable.Range(1, seeds).Select(seed => generator.Generate(new GeneratorOptions { Seed = seed, OldClientShare = share })).ToList()));
        }
        foreach (var conflict in new[] { 0.1, 0.5 })
        {
            groups.Add(new BenchmarkGroup(
                string.Format(CultureInfo.InvariantCulture, "Конфликтность {0:0}%, старых 50%", conflict * 100),
                Enumerable.Range(1, seeds).Select(seed => generator.Generate(new GeneratorOptions { Seed = 100 + seed, ConflictRate = conflict })).ToList()));
        }
        foreach (var offline in new[] { 1, 6 })
        {
            groups.Add(new BenchmarkGroup(
                $"Офлайн {offline} оп., старых 50%",
                Enumerable.Range(1, seeds).Select(seed => generator.Generate(new GeneratorOptions { Seed = 200 + seed, OfflineOperations = offline })).ToList()));
        }
        return groups;
    }

    public BenchmarkReport Run(IReadOnlyList<Func<ISyncStrategy>> strategies, IReadOnlyList<BenchmarkGroup> groups)
    {
        var oracle = new SyncOracle(registry);
        var replay = new StrategyReplay(registry);
        var names = strategies.Select(f => f().Name).ToList();
        var rows = new List<BenchmarkRow>();
        var perStrategy = names.ToDictionary(n => n, _ => new List<StrategyMetrics>());
        var ticks = names.ToDictionary(n => n, _ => 0L);
        var scenarios = 0;
        var events = 0;

        foreach (var warmup in groups.SelectMany(g => g.Scenarios).Take(3))
        {
            foreach (var factory in strategies)
                replay.Run(warmup.Input, factory());
        }

        foreach (var group in groups)
        {
            var groupMetrics = names.ToDictionary(n => n, _ => new List<StrategyMetrics>());
            var groupTicks = names.ToDictionary(n => n, _ => 0L);
            foreach (var scenario in group.Scenarios)
            {
                scenarios++;
                events += scenario.Input.Events.Count;
                var trace = OracleTrace.Build(oracle, scenario.Input);
                foreach (var factory in strategies)
                {
                    var timed = new TimedStrategy(factory());
                    var result = replay.Run(scenario.Input, timed);
                    var metrics = MetricsCalculator.Calculate(registry, timed.Name, scenario.Input, trace, result);
                    groupMetrics[timed.Name].Add(metrics);
                    perStrategy[timed.Name].Add(metrics);
                    groupTicks[timed.Name] += timed.Ticks;
                    ticks[timed.Name] += timed.Ticks;
                }
            }

            foreach (var name in names)
            {
                var sum = StrategyMetrics.Sum(name, groupMetrics[name]);
                rows.Add(new BenchmarkRow(group.Name, sum, Micros(groupTicks[name], sum.Operations)));
            }
        }

        foreach (var name in names)
        {
            var sum = StrategyMetrics.Sum(name, perStrategy[name]);
            rows.Add(new BenchmarkRow(TotalGroup, sum, Micros(ticks[name], sum.Operations)));
        }

        return new BenchmarkReport(names, rows, scenarios, events);
    }

    public static string ToMarkdown(BenchmarkReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Сравнение стратегий синхронизации");
        sb.AppendLine();
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "Сценариев: {0}, событий в журналах: {1}. Эталон — оракул; методика — `docs/method.md`, раздел «Мини-бенчмарк».",
            report.Scenarios, report.Events));
        sb.AppendLine();
        foreach (var group in report.Rows.Select(r => r.Group).Distinct().OrderBy(g => g == TotalGroup ? 0 : 1))
        {
            sb.AppendLine("## " + group);
            sb.AppendLine();
            sb.AppendLine("| Стратегия | Потеря из-за несовместимости версий | Корректно применённые изменения клиентов | Сохранённые данные сервера | Конфликты (S4), решённые верно | Расхождений с эталоном | Время решения, мкс/оп |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (var row in report.Group(group))
            {
                var m = row.Metrics;
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2} | {3} | {4} | {5} | {6:0.0} |",
                    m.Strategy,
                    Ratio(m.IncompatibilityLoss, m.IncompatibilityExposure),
                    Ratio(m.ClientChangesCorrect, m.ClientChanges),
                    Ratio(m.ServerCellsPreserved, m.ServerCells),
                    Ratio(m.ConflictsCorrect, m.Conflicts),
                    m.FinalMismatches,
                    row.MicrosecondsPerOperation));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string ToCsv(BenchmarkReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("group,strategy,operations,client_changes,client_changes_correct,conflicts,conflicts_correct," +
                      "unknown_cells,unknown_cells_lost,tombstones,tombstones_lost,server_cells,server_cells_preserved," +
                      "final_mismatches,microseconds_per_operation");
        foreach (var row in report.Rows)
        {
            var m = row.Metrics;
            sb.AppendLine(string.Join(",",
                "\"" + row.Group + "\"", m.Strategy, Int(m.Operations), Int(m.ClientChanges), Int(m.ClientChangesCorrect),
                Int(m.Conflicts), Int(m.ConflictsCorrect), Int(m.UnknownCells), Int(m.UnknownCellsLost), Int(m.Tombstones),
                Int(m.TombstonesLost), Int(m.ServerCells), Int(m.ServerCellsPreserved), Int(m.FinalMismatches),
                row.MicrosecondsPerOperation.ToString("0.00", CultureInfo.InvariantCulture)));
        }
        return sb.ToString();
    }

    private static string Ratio(int part, int total) =>
        total == 0 ? "—" : string.Format(CultureInfo.InvariantCulture, "{0:0.0}% ({1}/{2})", 100.0 * part / total, part, total);

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static double Micros(long ticks, int operations) =>
        operations == 0 ? 0 : ticks * 1_000_000.0 / Stopwatch.Frequency / operations;

    private sealed class TimedStrategy(ISyncStrategy inner) : ISyncStrategy
    {
        public long Ticks { get; private set; }

        public string Name => inner.Name;

        public StrategyDecision Decide(SyncContext context, SyncOperation operation, ServerRecord? current)
        {
            var start = Stopwatch.GetTimestamp();
            var decision = inner.Decide(context, operation, current);
            Ticks += Stopwatch.GetTimestamp() - start;
            return decision;
        }
    }
}
