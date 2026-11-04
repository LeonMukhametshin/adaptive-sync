#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AdaptiveSync.Demo.Mvvm;
using AdaptiveSync.Demo.Services;
using Sync.Core.Schema;

namespace AdaptiveSync.Demo.ViewModels
{
    public sealed class ProfileItem
    {
        public ProfileItem(Guid id, string nickname, string coins, string subtitle, IReadOnlyList<KeyValuePair<string, string>> fields, ICommand open)
        {
            Id = id;
            Nickname = nickname;
            Coins = coins;
            Subtitle = subtitle;
            Fields = fields;
            OpenCommand = open;
        }

        public Guid Id { get; }

        public string Nickname { get; }

        public string Coins { get; }

        public string Subtitle { get; }

        public IReadOnlyList<KeyValuePair<string, string>> Fields { get; }

        public ICommand OpenCommand { get; }
    }

    public sealed class ProfilesViewModel : ViewModelBase
    {
        private readonly SessionService _sessions;

        public ProfilesViewModel(SessionService sessions, IAppNotifier notifier)
        {
            _sessions = sessions;
            Editor = new ProfileEditorViewModel(sessions, notifier);
            CreateCommand = new Command(() => Editor.Open(null));
        }

        public ObservableList<ProfileItem> Items { get; } = new ObservableList<ProfileItem>();

        public Observable<bool> IsEmpty { get; } = new Observable<bool>(true);

        public ProfileEditorViewModel Editor { get; }

        public ICommand CreateCommand { get; }

        public void Refresh()
        {
            var profiles = _sessions.Current.Profiles;
            Items.Replace(profiles.Select(p =>
            {
                var id = p.RecordId;
                var fields = p.Values
                    .Where(v => v.Key != "nickname" && v.Key != "coins")
                    .Select(v => new KeyValuePair<string, string>(v.Key, Display.Value(v.Value)))
                    .ToList();
                return new ProfileItem(
                    id,
                    p.Values["nickname"] as string ?? "—",
                    Display.Value(p.Values["coins"]),
                    "id " + Display.Short(id) + " · row_version " + p.RowVersion,
                    fields,
                    new Command(() => Editor.Open(id)));
            }));
            IsEmpty.Value = profiles.Count == 0;
        }
    }

    public sealed class ProfileEditorViewModel : ViewModelBase
    {
        public const string AvatarField = "avatar_id";
        public const string PremiumField = "premium_currency";
        public const string RegionField = "region";

        private readonly SessionService _sessions;
        private readonly IAppNotifier _notifier;
        private Guid? _editing;

        public ProfileEditorViewModel(SessionService sessions, IAppNotifier notifier)
        {
            _sessions = sessions;
            _notifier = notifier;
            SaveCommand = new Command(Save);
            CancelCommand = new Command(Close);
            DeleteCommand = new Command(Delete);
        }

        public Observable<bool> IsOpen { get; } = new Observable<bool>(false);

        public Observable<bool> IsEditing { get; } = new Observable<bool>(false);

        public Observable<string> Title { get; } = new Observable<string>("");

        public Observable<string> Nickname { get; } = new Observable<string>("");

        public Observable<string> Coins { get; } = new Observable<string>("0");

        public Observable<string> Avatar { get; } = new Observable<string>("");

        public Observable<string> Premium { get; } = new Observable<string>("0");

        public Observable<string> Region { get; } = new Observable<string>("");

        public Observable<bool> ShowV2Fields { get; } = new Observable<bool>(false);

        public Observable<bool> ShowRegion { get; } = new Observable<bool>(false);

        public Observable<string> Hint { get; } = new Observable<string>("");

        public Observable<string> Error { get; } = new Observable<string>("");

        public ICommand SaveCommand { get; }

        public ICommand CancelCommand { get; }

        public ICommand DeleteCommand { get; }

        public Guid? Editing => _editing;

        public void Open(Guid? id)
        {
            var client = _sessions.Current.Client;
            _editing = id;
            var profile = id.HasValue ? client.Find(SchemaVersions.PlayerProfile, id.Value) : null;
            Title.Value = profile == null ? "Новый профиль" : "Изменить профиль";
            IsEditing.Value = profile != null;
            Nickname.Value = profile?.Values["nickname"] as string ?? "";
            Coins.Value = Convert.ToString(profile?.Values["coins"] ?? 0L, CultureInfo.InvariantCulture) ?? "0";

            ShowV2Fields.Value = Knows(AvatarField);
            ShowRegion.Value = Knows(RegionField);
            Avatar.Value = ShowV2Fields.Value ? profile?.Values[AvatarField] as string ?? "" : "";
            Premium.Value = ShowV2Fields.Value
                ? Convert.ToString(profile?.Values[PremiumField] ?? 0L, CultureInfo.InvariantCulture) ?? "0"
                : "0";
            Region.Value = ShowRegion.Value ? profile?.Values[RegionField] as string ?? "" : "";
            Hint.Value = ShowRegion.Value
                ? "Клиент v" + client.SchemaVersion + " знает все поля профиля"
                : "Клиент v" + client.SchemaVersion + " не знает полей " + AvatarField + ", " + PremiumField + ", " + RegionField +
                  " — их значения хранятся только на сервере";
            Error.Value = "";
            IsOpen.Value = true;
        }

        public void Close()
        {
            _editing = null;
            Error.Value = "";
            IsEditing.Value = false;
            IsOpen.Value = false;
        }

        private bool Knows(string field)
        {
            var client = _sessions.Current.Client;
            return client.Registry.IsKnown(client.SchemaVersion, SchemaVersions.PlayerProfile, field);
        }

        private void Save()
        {
            var nickname = Nickname.Value.Trim();
            if (nickname.Length == 0)
            {
                Error.Value = "Введите никнейм";
                return;
            }
            if (!long.TryParse(Coins.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var coins))
            {
                Error.Value = "Монеты — целое число";
                return;
            }

            var values = new Dictionary<string, object?> { ["nickname"] = nickname, ["coins"] = coins };
            if (ShowV2Fields.Value)
            {
                if (!long.TryParse(Premium.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var premium))
                {
                    Error.Value = "Премиальная валюта — целое число";
                    return;
                }
                values[AvatarField] = OrNull(Avatar.Value);
                values[PremiumField] = premium;
            }
            if (ShowRegion.Value)
                values[RegionField] = OrNull(Region.Value);

            Run(session =>
            {
                if (_editing.HasValue)
                    session.EditProfile(_editing.Value, values);
                else
                    session.CreateProfile(values);
            });
        }

        private void Delete()
        {
            if (!_editing.HasValue) return;
            var id = _editing.Value;
            Run(session => session.DeleteProfile(id));
        }

        private void Run(Action<SyncDemoSession> action)
        {
            try
            {
                var session = _sessions.Current;
                action(session);
                Close();
                _notifier.Toast(session.IsOnline ? "Записано в журнал — синхронизируйте" : "Записано в журнал (офлайн)", false);
                _notifier.Invalidate();
            }
            catch (Exception ex)
            {
                Error.Value = ex.Message;
            }
        }

        private static string? OrNull(string value)
        {
            var trimmed = value.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
    }
}
