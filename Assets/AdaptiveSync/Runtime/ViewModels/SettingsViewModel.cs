#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AdaptiveSync.Demo.App;
using AdaptiveSync.Demo.Mvvm;
using AdaptiveSync.Demo.Services;
using Sync.Core.Schema;

namespace AdaptiveSync.Demo.ViewModels
{
    public sealed class VersionOption
    {
        public VersionOption(int version, string label, ICommand select)
        {
            Version = version;
            Label = label;
            SelectCommand = select;
        }

        public int Version { get; }

        public string Label { get; }

        public Observable<bool> IsSelected { get; } = new Observable<bool>(false);

        public ICommand SelectCommand { get; }
    }

    public sealed class StrategyOption
    {
        public StrategyOption(string name, bool isSelected, ICommand select)
        {
            Name = name;
            Label = Display.StrategyLabel(name);
            IsSelected = isSelected;
            SelectCommand = select;
        }

        public string Name { get; }

        public string Label { get; }

        public bool IsSelected { get; }

        public ICommand SelectCommand { get; }
    }

    public sealed class SettingsViewModel : ViewModelBase
    {
        private readonly SessionService _sessions;
        private readonly ServerStatus _server;
        private readonly IAppNotifier _notifier;

        public SettingsViewModel(SessionService sessions, ServerStatus server, IAppNotifier notifier)
        {
            _sessions = sessions;
            _server = server;
            _notifier = notifier;
            ServerUrl = new Observable<string>(sessions.ServerUrl);
            ApplyServerUrlCommand = new Command(ApplyServerUrl);
            CheckServerCommand = new AsyncCommand(CheckServerAsync);
            ResetDatabaseCommand = new Command(ResetDatabase);
            Versions = AppSettings.ClientVersions
                .Select(v => new VersionOption(v, v == SchemaVersions.V1 ? "v1 · старая схема" : "v" + v + " · текущая схема",
                    new Command(() => SwitchVersion(v))))
                .ToList();
        }

        public Observable<string> ServerUrl { get; }

        public Observable<string> ServerHealth { get; } = new Observable<string>("");

        public Observable<string> ClientInfo { get; } = new Observable<string>("");

        public Observable<string> DatabasePath { get; } = new Observable<string>("");

        public IReadOnlyList<VersionOption> Versions { get; }

        public static readonly IReadOnlyList<string> StrategyOrder = new[] { "Adaptive", "ServerWins", "Lww", "ClientWins", "FieldLwwNoSchema" };

        public ObservableList<StrategyOption> Strategies { get; } = new ObservableList<StrategyOption>();

        public Observable<bool> CanSwitchStrategy { get; } = new Observable<bool>(false);

        public Observable<string> StrategyHint { get; } = new Observable<string>("");

        public ICommand ApplyServerUrlCommand { get; }

        public AsyncCommand CheckServerCommand { get; }

        public ICommand ResetDatabaseCommand { get; }

        public void Refresh()
        {
            var session = _sessions.Current;
            var client = session.Client;
            foreach (var option in Versions)
                option.IsSelected.Value = option.Version == client.SchemaVersion;
            ClientInfo.Value = "Поля схемы v" + client.SchemaVersion + ": " + string.Join(", ",
                client.Registry.KnownFields(client.SchemaVersion).Where(f => f.Table == SchemaVersions.PlayerProfile).Select(f => f.Field));
            DatabasePath.Value = session.DatabasePath;
            ServerHealth.Value = DescribeServer(client.SchemaVersion);
            RefreshStrategies();
            if (ServerUrl.Value.Length == 0) ServerUrl.Value = _sessions.ServerUrl;
        }

        private string DescribeServer(int clientVersion) =>
            !_server.IsChecked ? "Сервер ещё не проверялся"
            : _server.IsReachable
                ? "Сервер доступен · схема сервера v" + _server.SchemaVersion + ", клиента v" + clientVersion +
                  " · стратегия " + Display.StrategyLabel(_server.Strategy) + DescribeSource()
            : "Нет соединения: " + _server.Error;

        private string DescribeSource() =>
            _server.StrategySource == "resolver" ? " (сервис resolver)"
            : _server.StrategySource == "builtin" ? " (встроенная)"
            : "";

        private void RefreshStrategies()
        {
            CanSwitchStrategy.Value = _server.IsReachable && _server.CanSwitch;
            var available = StrategyOrder.Where(n => _server.Strategies.Contains(n))
                .Concat(_server.Strategies.Where(n => !StrategyOrder.Contains(n)))
                .ToList();
            var current = Strategies.Items.Select(o => (o.Name, o.IsSelected)).ToList();
            var next = available.Select(n => (n, n == _server.Strategy)).ToList();
            if (!current.SequenceEqual(next))
                Strategies.Replace(available.Select(n => new StrategyOption(n, n == _server.Strategy, new AsyncCommand(() => SwitchStrategyAsync(n)))));
            StrategyHint.Value = CanSwitchStrategy.Value
                ? "Метод разрешения конфликтов на сервере. Повторите те же действия на другой стратегии и сравните результат."
                : "";
        }

        private async Task SwitchStrategyAsync(string strategy)
        {
            if (_notifier.IsBusy || strategy == _server.Strategy) return;
            try
            {
                await _server.SwitchStrategyAsync(_sessions.ServerUrl, strategy);
                _notifier.Toast("Стратегия сервера: " + Display.StrategyLabel(_server.Strategy), false);
            }
            catch (Exception ex)
            {
                _notifier.Toast(ex.Message, true);
            }
            _notifier.Invalidate();
        }

        private void ApplyServerUrl()
        {
            var before = _sessions.ServerUrl;
            if (Run(() => _sessions.ChangeServerUrl(ServerUrl.Value)) && _sessions.ServerUrl != before)
                _notifier.Toast("Адрес сервера сохранён", false);
            ServerUrl.Value = _sessions.ServerUrl;
        }

        private async Task CheckServerAsync()
        {
            ApplyServerUrl();
            await _server.CheckAsync(_sessions.ServerUrl);
            _notifier.Invalidate();
            _notifier.Toast(_server.IsReachable
                ? "Соединение проверено · стратегия " + Display.StrategyLabel(_server.Strategy)
                : "Нет соединения: " + _server.Error, !_server.IsReachable);
        }

        private void SwitchVersion(int version)
        {
            if (_notifier.IsBusy || _sessions.SchemaVersion == version) return;
            if (Run(() => _sessions.Open(version)))
                _notifier.Toast("Клиент v" + version + " · своя локальная БД", false);
        }

        private void ResetDatabase()
        {
            if (_notifier.IsBusy) return;
            if (Run(() => _sessions.ResetDatabase()))
                _notifier.Toast("Локальная БД клиента v" + _sessions.SchemaVersion + " очищена", false);
        }

        private bool Run(Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                _notifier.Toast(ex.Message, true);
                return false;
            }
        }
    }
}
