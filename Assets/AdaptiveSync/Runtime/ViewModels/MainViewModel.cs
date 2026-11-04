#nullable enable
using System.Collections.Generic;
using System.Linq;
using AdaptiveSync.Demo.Mvvm;
using AdaptiveSync.Demo.Services;

namespace AdaptiveSync.Demo.ViewModels
{
    public sealed class MainViewModel : ViewModelBase, IAppNotifier
    {
        public const string ProfilesTab = "profiles";
        public const string SyncTab = "sync";
        public const string JournalTab = "journal";
        public const string SettingsTab = "settings";

        public static readonly IReadOnlyList<string> Tabs = new[] { ProfilesTab, SyncTab, JournalTab, SettingsTab };

        private readonly SessionService _sessions;
        private readonly ServerStatus _server;
        private readonly Dictionary<string, Observable<bool>> _tabSelected;

        public MainViewModel(SessionService sessions, ServerStatus server)
        {
            _sessions = sessions;
            _server = server;
            _tabSelected = Tabs.ToDictionary(t => t, t => new Observable<bool>(t == ProfilesTab));

            Profiles = Disposables.Add(new ProfilesViewModel(sessions, this));
            Sync = Disposables.Add(new SyncViewModel(sessions, this));
            Journal = Disposables.Add(new JournalViewModel(sessions));
            Settings = Disposables.Add(new SettingsViewModel(sessions, server, this));

            ToggleOnlineCommand = new Command(ToggleOnline);
            SelectTabCommand = new Command<string>(tab => SelectedTab.Value = tab);
            Disposables.Add(SelectedTab.Subscribe(tab =>
            {
                foreach (var pair in _tabSelected) pair.Value.Value = pair.Key == tab;
            }));

            _sessions.Changed += Invalidate;
            _server.Changed += Invalidate;
            Disposables.Add(new Subscription(() =>
            {
                _sessions.Changed -= Invalidate;
                _server.Changed -= Invalidate;
            }));

            Invalidate();
        }

        public ProfilesViewModel Profiles { get; }

        public SyncViewModel Sync { get; }

        public JournalViewModel Journal { get; }

        public SettingsViewModel Settings { get; }

        public Observable<string> SelectedTab { get; } = new Observable<string>(ProfilesTab);

        public Observable<string> ClientLabel { get; } = new Observable<string>("");

        public Observable<bool> IsOnline { get; } = new Observable<bool>(true);

        public Observable<string> NetworkText { get; } = new Observable<string>("");

        public Observable<string> PendingBadge { get; } = new Observable<string>("0");

        public Observable<bool> HasPending { get; } = new Observable<bool>(false);

        public Observable<ToastMessage?> Toast { get; } = new Observable<ToastMessage?>(null);

        public ICommand ToggleOnlineCommand { get; }

        public Command<string> SelectTabCommand { get; }

        public bool IsBusy => Sync.IsBusy.Value;

        public IReadOnlyObservable<bool> IsTabSelected(string tab) => _tabSelected[tab];

        public void Invalidate()
        {
            if (!_sessions.IsOpen) return;
            var session = _sessions.Current;
            var client = session.Client;
            ClientLabel.Value = client.ClientId + " · схема v" + client.SchemaVersion +
                                (_server.IsChecked && !_server.IsReachable ? " · сервер недоступен" : "");
            IsOnline.Value = session.IsOnline;
            NetworkText.Value = session.IsOnline ? "● Online" : "● Offline";
            var pending = client.PendingOperationCount;
            PendingBadge.Value = Display.Number(pending);
            HasPending.Value = pending > 0;

            Profiles.Refresh();
            Sync.Refresh();
            Journal.Refresh();
            Settings.Refresh();
        }

        void IAppNotifier.Toast(string text, bool isError) => Toast.Value = new ToastMessage(text, isError);

        public void ToggleOnline()
        {
            var session = _sessions.Current;
            session.IsOnline = !session.IsOnline;
            Toast.Value = new ToastMessage(session.IsOnline ? "Сеть включена" : "Офлайн: изменения копятся в журнале", false);
            Invalidate();
        }
    }
}
