#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AdaptiveSync.Demo.Services
{
    public sealed class ServerStatus
    {
        public bool IsChecked { get; private set; }

        public bool IsReachable { get; private set; }

        public int SchemaVersion { get; private set; }

        public string Strategy { get; private set; } = "";

        public string StrategySource { get; private set; } = "";

        public bool CanSwitch { get; private set; }

        public IReadOnlyList<string> Strategies { get; private set; } = Array.Empty<string>();

        public string? Error { get; private set; }

        public event Action? Changed;

        public async Task CheckAsync(string serverUrl)
        {
            try
            {
                Apply(await SyncDemoSession.CheckHealthAsync(serverUrl));
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
        }

        public async Task SwitchStrategyAsync(string serverUrl, string strategy)
        {
            await SyncDemoSession.SwitchStrategyAsync(serverUrl, strategy);
            await CheckAsync(serverUrl);
        }

        public void Apply(ServerHealth health)
        {
            IsReachable = true;
            SchemaVersion = health.SchemaVersion;
            Strategy = health.Strategy;
            StrategySource = health.StrategySource;
            CanSwitch = health.CanSwitch;
            Strategies = health.Strategies;
            Error = null;
            IsChecked = true;
            Changed?.Invoke();
        }

        public void Fail(string error)
        {
            IsReachable = false;
            CanSwitch = false;
            Strategies = Array.Empty<string>();
            Error = error;
            IsChecked = true;
            Changed?.Invoke();
        }
    }
}
