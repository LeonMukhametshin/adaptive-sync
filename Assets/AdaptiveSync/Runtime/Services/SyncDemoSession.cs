#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Sync.Client;
using Sync.Client.Transport;
using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Time;

namespace AdaptiveSync.Demo.Services
{
    public sealed class SyncDemoSession : IDisposable
    {
        private readonly HttpClient _http;

        public SyncDemoSession(
            string serverUrl, string databasePath, string clientId, int schemaVersion, LogicalClock clock, Func<Guid> newId)
        {
            ServerUrl = NormalizeUrl(serverUrl);
            DatabasePath = databasePath;
            _http = new HttpClient { BaseAddress = new Uri(ServerUrl), Timeout = TimeSpan.FromSeconds(10) };
            Client = SyncClient.Open(databasePath, clientId, schemaVersion, new HttpSyncTransport(_http), clock, newId);
        }

        public SyncClient Client { get; }

        public string ServerUrl { get; }

        public string DatabasePath { get; }

        public bool IsOnline
        {
            get => Client.IsOnline;
            set => Client.IsOnline = value;
        }

        public SyncResponse? LastResponse { get; private set; }

        public string? LastError { get; private set; }

        public IReadOnlyList<RecordState> Profiles => Client.GetAll(SchemaVersions.PlayerProfile);

        public IReadOnlyList<SyncOperation> PendingOperations => Client.PendingOperations();

        public Guid CreateProfile(string nickname, long coins) =>
            CreateProfile(new Dictionary<string, object?> { ["nickname"] = nickname, ["coins"] = coins });

        public Guid CreateProfile(IReadOnlyDictionary<string, object?> values) =>
            Client.Insert(SchemaVersions.PlayerProfile, values);

        public void EditProfile(Guid id, string nickname, long coins) =>
            EditProfile(id, new Dictionary<string, object?> { ["nickname"] = nickname, ["coins"] = coins });

        public void EditProfile(Guid id, IReadOnlyDictionary<string, object?> values) =>
            Client.Update(SchemaVersions.PlayerProfile, id, values);

        public void DeleteProfile(Guid id) => Client.Delete(SchemaVersions.PlayerProfile, id);

        public async Task<bool> SyncAsync()
        {
            try
            {
                LastResponse = await Client.SyncAsync();
                LastError = null;
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is HttpRequestException || ex is TaskCanceledException)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public static async Task<ServerHealth> CheckHealthAsync(string serverUrl)
        {
            using var http = new HttpClient { BaseAddress = new Uri(NormalizeUrl(serverUrl)), Timeout = TimeSpan.FromSeconds(5) };
            var text = await http.GetStringAsync("health");
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var strategies = new List<string>();
            if (root.TryGetProperty("strategies", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                    strategies.Add(item.GetString() ?? "");
            }
            return new ServerHealth(
                root.GetProperty("schemaVersion").GetInt32(),
                root.TryGetProperty("strategy", out var strategy) ? strategy.GetString() ?? "" : "",
                root.TryGetProperty("strategySource", out var source) ? source.GetString() ?? "" : "",
                root.TryGetProperty("strategySwitch", out var canSwitch) && canSwitch.ValueKind == JsonValueKind.True,
                strategies);
        }

        public static async Task SwitchStrategyAsync(string serverUrl, string strategy)
        {
            using var http = new HttpClient { BaseAddress = new Uri(NormalizeUrl(serverUrl)), Timeout = TimeSpan.FromSeconds(5) };
            var body = "{\"strategy\":" + JsonSerializer.Serialize(strategy) + "}";
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("admin/strategy", content);
            if (response.IsSuccessStatusCode) return;

            var text = await response.Content.ReadAsStringAsync();
            var error = text;
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.TryGetProperty("error", out var message)) error = message.GetString() ?? text;
            }
            catch (JsonException)
            {
            }
            throw new InvalidOperationException("Сервер не сменил стратегию (" + (int)response.StatusCode + "): " + error);
        }

        public static string NormalizeUrl(string serverUrl)
        {
            var url = serverUrl.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "http://" + url;
            }
            return url.EndsWith("/") ? url : url + "/";
        }

        public void Dispose()
        {
            Client.Dispose();
            _http.Dispose();
        }
    }

    public readonly struct ServerHealth
    {
        public ServerHealth(int schemaVersion, string strategy, string strategySource)
            : this(schemaVersion, strategy, strategySource, false, Array.Empty<string>())
        {
        }

        public ServerHealth(int schemaVersion, string strategy, string strategySource, bool canSwitch, IReadOnlyList<string> strategies)
        {
            SchemaVersion = schemaVersion;
            Strategy = strategy;
            StrategySource = strategySource;
            CanSwitch = canSwitch;
            Strategies = strategies;
        }

        public int SchemaVersion { get; }

        public string Strategy { get; }

        public string StrategySource { get; }

        public bool CanSwitch { get; }

        public IReadOnlyList<string> Strategies { get; }
    }
}
