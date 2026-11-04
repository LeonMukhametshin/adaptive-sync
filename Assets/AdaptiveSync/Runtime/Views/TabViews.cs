#nullable enable
using System;
using AdaptiveSync.Demo.Mvvm;
using AdaptiveSync.Demo.UI;
using AdaptiveSync.Demo.ViewModels;
using UnityEngine.UIElements;

namespace AdaptiveSync.Demo.Views
{
    public sealed class ProfilesView : IDisposable
    {
        private readonly CompositeDisposable _bindings = new CompositeDisposable();

        public ProfilesView(VisualElement root, ProfilesViewModel viewModel)
        {
            _bindings.Add(Bind.Command(UiKit.Find<Button>(root, "create-profile"), viewModel.CreateCommand));
            _bindings.Add(Bind.Visible(UiKit.Find<VisualElement>(root, "profiles-empty"), viewModel.IsEmpty));
            _bindings.Add(Bind.Items(UiKit.Find<VisualElement>(root, "profile-list"), viewModel.Items, ProfileCard));

            var editor = viewModel.Editor;
            var overlay = UiKit.Find<VisualElement>(root, "sheet-overlay");
            _bindings.Add(Bind.Class(overlay, "overlay--open", editor.IsOpen));
            _bindings.Add(Bind.Text(UiKit.Find<Label>(root, "sheet-title"), editor.Title));
            _bindings.Add(Bind.TwoWay(UiKit.Find<TextField>(root, "nickname-field"), editor.Nickname));
            _bindings.Add(Bind.TwoWay(UiKit.Find<TextField>(root, "coins-field"), editor.Coins));
            _bindings.Add(Bind.TwoWay(UiKit.Find<TextField>(root, "avatar-field"), editor.Avatar));
            _bindings.Add(Bind.TwoWay(UiKit.Find<TextField>(root, "premium-field"), editor.Premium));
            _bindings.Add(Bind.TwoWay(UiKit.Find<TextField>(root, "region-field"), editor.Region));
            _bindings.Add(Bind.Visible(UiKit.Find<VisualElement>(root, "v2-fields"), editor.ShowV2Fields));
            _bindings.Add(Bind.Visible(UiKit.Find<VisualElement>(root, "region-field"), editor.ShowRegion));
            _bindings.Add(Bind.Text(UiKit.Find<Label>(root, "sheet-hint"), editor.Hint));
            var delete = UiKit.Find<Button>(root, "sheet-delete");
            _bindings.Add(Bind.Visible(delete, editor.IsEditing));
            _bindings.Add(Bind.Command(delete, editor.DeleteCommand));
            UiKit.Find<TextField>(root, "premium-field").keyboardType = UnityEngine.TouchScreenKeyboardType.NumberPad;
            _bindings.Add(Bind.Text(UiKit.Find<Label>(root, "sheet-error"), editor.Error));
            _bindings.Add(Bind.Command(UiKit.Find<Button>(root, "sheet-save"), editor.SaveCommand));
            _bindings.Add(Bind.Command(UiKit.Find<Button>(root, "sheet-cancel"), editor.CancelCommand));
            UiKit.Find<TextField>(root, "coins-field").keyboardType = UnityEngine.TouchScreenKeyboardType.NumberPad;

            EventCallback<PointerDownEvent> backdrop = e =>
            {
                if (e.target == overlay) editor.CancelCommand.Execute();
            };
            overlay.RegisterCallback(backdrop);
            _bindings.Add(new Subscription(() => overlay.UnregisterCallback(backdrop)));
        }

        public void Dispose() => _bindings.Dispose();

        private static VisualElement ProfileCard(ProfileItem item)
        {
            var card = UiKit.Card();
            card.AddToClassList("card--pressable");
            Bind.Tap(card, item.OpenCommand);

            var header = new VisualElement();
            header.AddToClassList("profile__header");
            var titles = new VisualElement();
            titles.Add(UiKit.Text(item.Nickname, "profile__name"));
            titles.Add(UiKit.Text(item.Subtitle, "item__text"));
            header.Add(titles);
            var coins = new VisualElement { style = { alignItems = Align.FlexEnd } };
            coins.Add(UiKit.Text(item.Coins, "profile__coins"));
            coins.Add(UiKit.Text("монеты", "profile__coins-caption"));
            header.Add(coins);
            card.Add(header);

            foreach (var field in item.Fields)
            {
                var row = new VisualElement();
                row.AddToClassList("kv");
                row.Add(UiKit.Text(field.Key, "kv__key"));
                row.Add(UiKit.Text(field.Value, "kv__value"));
                card.Add(row);
            }

            return card;
        }
    }

    public sealed class SyncView : IDisposable
    {
        private readonly CompositeDisposable _bindings = new CompositeDisposable();

        public SyncView(VisualElement root, SyncViewModel viewModel)
        {
            var onlineSwitch = UiKit.Find<VisualElement>(root, "online-switch");
            _bindings.Add(Bind.Class(onlineSwitch, "switch--on", viewModel.IsOnline));
            _bindings.Add(Bind.Tap(onlineSwitch, viewModel.ToggleOnlineCommand));
            _bindings.Add(Bind.Text(UiKit.Find<Label>(root, "network-caption"), viewModel.NetworkCaption));

            _bindings.Add(Bind.Command(UiKit.Find<Button>(root, "sync-button"), viewModel.SyncCommand));
            _bindings.Add(Bind.Class(UiKit.Find<VisualElement>(root, "sync-spinner"), "spinner--visible", viewModel.IsBusy));
            _bindings.Add(Bind.Text(UiKit.Find<Label>(root, "sync-button-label"), viewModel.SyncButtonText));

            var status = UiKit.Find<Label>(root, "sync-status");
            _bindings.Add(Bind.Text(status, viewModel.StatusText));
            _bindings.Add(viewModel.Status.Subscribe(kind =>
            {
                status.EnableInClassList("status--success", kind == StatusKind.Success);
                status.EnableInClassList("status--error", kind == StatusKind.Error);
            }));

            _bindings.Add(Bind.Text(UiKit.Find<Label>(root, "stat-pending"), viewModel.PendingText));
            _bindings.Add(Bind.Text(UiKit.Find<Label>(root, "stat-cursor"), viewModel.CursorText));
            _bindings.Add(Bind.Text(UiKit.Find<Label>(root, "stat-remote"), viewModel.RemoteText));
            _bindings.Add(Bind.Visible(UiKit.Find<VisualElement>(root, "results-empty"), viewModel.ResultsEmpty));
            _bindings.Add(Bind.Items(UiKit.Find<VisualElement>(root, "result-list"), viewModel.Results, ResultCard));
        }

        public void Dispose() => _bindings.Dispose();

        private static VisualElement ResultCard(ResultItem item)
        {
            var card = UiKit.Card();
            var header = UiKit.Row("row--spread");
            header.Add(UiKit.Text(item.Title, "item__title"));
            header.Add(UiKit.Badge(item.Outcome, item.OutcomeClass));
            card.Add(header);
            card.Add(UiKit.Text(item.Details, "item__text"));
            if (item.HasSituation) card.Add(UiKit.Text(item.Situation, "item__situation"));
            foreach (var field in item.Fields)
                card.Add(UiKit.Text(field, "item__change"));
            if (item.HasMessage) card.Add(UiKit.Text(item.Message, "item__text"));
            return card;
        }
    }

    public sealed class JournalView : IDisposable
    {
        private readonly CompositeDisposable _bindings = new CompositeDisposable();

        public JournalView(VisualElement root, JournalViewModel viewModel)
        {
            _bindings.Add(Bind.Text(UiKit.Find<Label>(root, "journal-summary"), viewModel.Summary));
            _bindings.Add(Bind.Visible(UiKit.Find<VisualElement>(root, "journal-empty"), viewModel.IsEmpty));
            _bindings.Add(Bind.Items(UiKit.Find<VisualElement>(root, "journal-list"), viewModel.Items, OperationCard));
        }

        public void Dispose() => _bindings.Dispose();

        private static VisualElement OperationCard(OperationItem item)
        {
            var card = UiKit.Card();
            var header = UiKit.Row("row--spread");
            header.Add(UiKit.Text(item.Title, "item__title"));
            header.Add(UiKit.Badge(item.Type, item.TypeClass));
            card.Add(header);
            card.Add(UiKit.Text(item.Details, "item__text"));
            foreach (var change in item.Changes)
                card.Add(UiKit.Text(change, "item__change"));
            return card;
        }
    }

    public sealed class SettingsView : IDisposable
    {
        private readonly CompositeDisposable _bindings = new CompositeDisposable();

        public SettingsView(VisualElement root, SettingsViewModel viewModel)
        {
            var serverField = UiKit.Find<TextField>(root, "server-url");
            _bindings.Add(Bind.TwoWay(serverField, viewModel.ServerUrl));
            EventCallback<FocusOutEvent> apply = _ => viewModel.ApplyServerUrlCommand.Execute();
            serverField.RegisterCallback(apply);
            _bindings.Add(new Subscription(() => serverField.UnregisterCallback(apply)));

            _bindings.Add(Bind.Command(UiKit.Find<Button>(root, "check-server"), viewModel.CheckServerCommand));
            _bindings.Add(Bind.Text(UiKit.Find<Label>(root, "server-health"), viewModel.ServerHealth));
            _bindings.Add(Bind.Visible(UiKit.Find<VisualElement>(root, "strategy-card"), viewModel.CanSwitchStrategy));
            _bindings.Add(Bind.Text(UiKit.Find<Label>(root, "strategy-hint"), viewModel.StrategyHint));
            _bindings.Add(Bind.Items(UiKit.Find<VisualElement>(root, "strategy-list"), viewModel.Strategies, StrategyChip));

            foreach (var option in viewModel.Versions)
            {
                var button = UiKit.Find<Button>(root, "version-v" + option.Version);
                button.text = option.Label;
                _bindings.Add(Bind.Class(button, "segmented__item--active", option.IsSelected));
                _bindings.Add(Bind.Command(button, option.SelectCommand));
            }

            _bindings.Add(Bind.Text(UiKit.Find<Label>(root, "client-info"), viewModel.ClientInfo));
            _bindings.Add(Bind.Text(UiKit.Find<Label>(root, "db-path"), viewModel.DatabasePath));
            _bindings.Add(Bind.Command(UiKit.Find<Button>(root, "reset-db"), viewModel.ResetDatabaseCommand));
        }

        public void Dispose() => _bindings.Dispose();

        private static VisualElement StrategyChip(StrategyOption option)
        {
            var chip = new Button { text = option.Label };
            chip.AddToClassList("chip");
            chip.EnableInClassList("chip--active", option.IsSelected);
            chip.clicked += option.SelectCommand.Execute;
            return chip;
        }
    }
}
