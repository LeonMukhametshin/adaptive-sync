using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;
using Sync.Core.Schema;
using Sync.Core.Strategies;
using Sync.Simulation;
using Sync.Strategies;

namespace Sync.Bench;

public sealed record EndToEndMode(string Name, string Strategy, bool ViaResolver);

public sealed record EndToEndResult(EndToEndMode Mode, IReadOnlyList<RequestSample> Requests, int Scenarios, int Mismatches);

public sealed class EndToEndBenchmark(string repositoryRoot, string adminConnectionString)
{
    public static readonly IReadOnlyList<EndToEndMode> DefaultModes = new EndToEndMode[]
    {
        new("Adaptive (встроенная)", AdaptiveStrategy.StrategyName, false),
        new("Adaptive (resolver)", AdaptiveStrategy.StrategyName, true),
        new("ServerWins", ServerWinsStrategy.StrategyName, false),
        new("Lww", LwwStrategy.StrategyName, false),
        new("ClientWins", ClientWinsStrategy.StrategyName, false),
        new("FieldLwwNoSchema", FieldLwwNoSchemaStrategy.StrategyName, false),
    };

    public async Task<IReadOnlyList<EndToEndResult>> RunAsync(IReadOnlyList<EndToEndMode> modes, IReadOnlyList<Scenario> scenarios, Scenario warmup)
    {
        var results = new List<EndToEndResult>();
        foreach (var mode in modes)
        {
            Console.WriteLine("e2e: " + mode.Name);
            var database = "bench_e2e_" + mode.Strategy.ToLowerInvariant() + (mode.ViaResolver ? "_resolver" : "");
            var connectionString = await CreateDatabaseAsync(database);
            var processes = new List<Process>();
            try
            {
                var serverPort = FreePort();
                var serverArgs = new List<string>
                {
                    "--urls", "http://127.0.0.1:" + serverPort,
                    "--ConnectionStrings:Sync=" + connectionString,
                    "--Sync:Strategy=" + mode.Strategy,
                    "--Logging:LogLevel:Default=Warning",
                };
                if (mode.ViaResolver)
                {
                    var resolverPort = FreePort();
                    processes.Add(Start("Sync.Resolver", new[]
                    {
                        "--urls", "http://127.0.0.1:" + resolverPort, "--Resolver:Strategy=" + mode.Strategy, "--Logging:LogLevel:Default=Warning",
                    }));
                    await WaitHealthyAsync("http://127.0.0.1:" + resolverPort + "/");
                    serverArgs.Add("--Sync:ResolverUrl=http://127.0.0.1:" + resolverPort + "/");
                }
                processes.Add(Start("Sync.Server", serverArgs));
                var baseAddress = "http://127.0.0.1:" + serverPort + "/";
                await WaitHealthyAsync(baseAddress);

                using var http = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(60) };
                var driver = new EndToEndDriver(http, connectionString, SchemaVersions.Registry);
                var replay = new StrategyReplay(SchemaVersions.Registry);
                await driver.RunAsync(warmup.Input, replay.Run(warmup.Input, SyncStrategies.Create(mode.Strategy)));

                var samples = new List<RequestSample>();
                var mismatches = 0;
                foreach (var scenario in scenarios)
                {
                    var run = await driver.RunAsync(scenario.Input, replay.Run(scenario.Input, SyncStrategies.Create(mode.Strategy)));
                    samples.AddRange(run.Requests);
                    mismatches += run.Mismatches.Count;
                    foreach (var mismatch in run.Mismatches.Take(5))
                        Console.WriteLine("  mismatch " + scenario.Name + ": " + mismatch);
                }
                results.Add(new EndToEndResult(mode, samples, scenarios.Count, mismatches));
            }
            finally
            {
                foreach (var process in processes)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.Dispose();
                }
                NpgsqlConnection.ClearAllPools();
                await DropDatabaseAsync(database);
            }
        }
        return results;
    }

    public static string ToMarkdown(IReadOnlyList<EndToEndResult> results, string environment)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Сквозной замер: время синхронизации и объём данных");
        sb.AppendLine();
        sb.AppendLine(environment);
        sb.AppendLine();
        sb.AppendLine("## Отправка изменений (push)");
        sb.AppendLine();
        sb.AppendLine("| Режим | Запросов | Операций в пакете | Время, мс: медиана | Время, мс: p95 | Сервер, мс (среднее) | Метод, мс (среднее) | Доля метода во времени сервера | Запрос, байт | Ответ, байт | Байт на операцию | Расхождений с моделью |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var r in results)
        {
            var push = r.Requests.Where(q => q.Kind == EndToEndDriver.Push).ToList();
            var operations = push.Sum(q => q.Operations);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "| {0} | {1} | {2:0.0} | {3:0.00} | {4:0.00} | {5:0.00} | {6:0.000} | {7:0.0}% | {8:0} | {9:0} | {10:0} | {11} |",
                r.Mode.Name, push.Count, push.Average(q => q.Operations), Percentile(push.Select(q => q.WallMs), 50),
                Percentile(push.Select(q => q.WallMs), 95), push.Average(q => q.ServerMs), push.Average(q => q.DecideMs),
                100 * push.Sum(q => q.DecideMs) / Math.Max(1e-9, push.Sum(q => q.ServerMs)), push.Average(q => q.RequestBytes),
                push.Average(q => q.ResponseBytes), push.Sum(q => q.RequestBytes + q.ResponseBytes) / (double)Math.Max(1, operations),
                r.Mismatches));
        }
        sb.AppendLine();
        sb.AppendLine("## Получение изменений (pull)");
        sb.AppendLine();
        sb.AppendLine("| Режим | Запросов | Время, мс: медиана | Время, мс: p95 | Сервер, мс (среднее) | Ответ, байт (среднее) |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var r in results)
        {
            var pull = r.Requests.Where(q => q.Kind == EndToEndDriver.Pull).ToList();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2:0.00} | {3:0.00} | {4:0.00} | {5:0} |",
                r.Mode.Name, pull.Count, Percentile(pull.Select(q => q.WallMs), 50), Percentile(pull.Select(q => q.WallMs), 95),
                pull.Average(q => q.ServerMs), pull.Average(q => q.ResponseBytes)));
        }
        return sb.ToString();
    }

    public static string ToCsv(IReadOnlyList<EndToEndResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("mode,strategy,via_resolver,kind,operations,request_bytes,response_bytes,wall_ms,server_ms,decide_ms,decisions");
        foreach (var r in results)
        {
            foreach (var q in r.Requests)
            {
                sb.AppendLine(string.Join(",", "\"" + r.Mode.Name + "\"", r.Mode.Strategy, r.Mode.ViaResolver ? "true" : "false", q.Kind,
                    I(q.Operations), I(q.RequestBytes), I(q.ResponseBytes), D(q.WallMs), D(q.ServerMs), D(q.DecideMs), I(q.Decisions)));
            }
        }
        return sb.ToString();
    }

    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return double.NaN;
        var rank = percentile / 100 * (sorted.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }

    private Process Start(string project, IEnumerable<string> arguments)
    {
        var directory = Path.Combine(repositoryRoot, "src", project, "bin", "Release", "net8.0");
        var executable = Path.Combine(directory, project + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(executable))
            throw new FileNotFoundException($"{executable} not found; build the solution with 'dotnet build -c Release' first.");

        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start " + project);
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitHealthyAsync(string baseAddress)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var response = await http.GetAsync("health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }
            await Task.Delay(200);
        }
        throw new TimeoutException(baseAddress + " did not become healthy.");
    }

    private async Task<string> CreateDatabaseAsync(string database)
    {
        await DropDatabaseAsync(database);
        await using var dataSource = NpgsqlDataSource.Create(adminConnectionString);
        await using (var command = dataSource.CreateCommand($"CREATE DATABASE {database}"))
            await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = database }.ConnectionString;
    }

    private async Task DropDatabaseAsync(string database)
    {
        await using var dataSource = NpgsqlDataSource.Create(adminConnectionString);
        await using var command = dataSource.CreateCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        await command.ExecuteNonQueryAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string D(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
