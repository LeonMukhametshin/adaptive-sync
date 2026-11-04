using System;
using System.Collections;
using System.IO;
using System.Linq;
using AdaptiveSync.Demo.App;
using AdaptiveSync.Demo.Boot;
using AdaptiveSync.Demo.Services;
using AdaptiveSync.Demo.ViewModels;
using NUnit.Framework;
using Sync.Core.Schema;
using UnityEngine.TestTools;

namespace AdaptiveSync.Demo.EditModeTests
{
    public sealed class ViewModelTests
    {
        private string _directory;
        private SessionService _sessions;
        private ServerStatus _server;
        private MainViewModel _main;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "adaptive-sync-vm-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _sessions = new SessionService(AppSettings.InMemory("http://127.0.0.1:9/", SchemaVersions.V1), _directory);
            _sessions.Open(SchemaVersions.V1);
            _server = new ServerStatus();
            _main = new MainViewModel(_sessions, _server);
        }

        [TearDown]
        public void TearDown()
        {
            _main.Dispose();
            _sessions.Dispose();
            try
            {
                Directory.Delete(_directory, true);
            }
            catch (IOException)
            {
            }
        }

        [Test]
        public void Main_ShowsClientAndStartsOnProfiles()
        {
            Assert.AreEqual("unity-v1 · схема v1", _main.ClientLabel.Value);
            Assert.AreEqual("● Online", _main.NetworkText.Value);
            Assert.IsTrue(_main.IsTabSelected(MainViewModel.ProfilesTab).Value);

            _main.SelectTabCommand.Execute(MainViewModel.SyncTab);

            Assert.IsFalse(_main.IsTabSelected(MainViewModel.ProfilesTab).Value);
            Assert.IsTrue(_main.IsTabSelected(MainViewModel.SyncTab).Value);
            Assert.IsTrue(_main.Profiles.IsEmpty.Value);
            Assert.IsFalse(_main.HasPending.Value);
        }

        [Test]
        public void Editor_ValidatesInputBeforeSaving()
        {
            var editor = _main.Profiles.Editor;
            _main.Profiles.CreateCommand.Execute();
            Assert.IsTrue(editor.IsOpen.Value);
            Assert.AreEqual("Новый профиль", editor.Title.Value);

            editor.Nickname.Value = "   ";
            editor.SaveCommand.Execute();
            Assert.AreEqual("Введите никнейм", editor.Error.Value);

            editor.Nickname.Value = "neo";
            editor.Coins.Value = "много";
            editor.SaveCommand.Execute();
            Assert.AreEqual("Монеты — целое число", editor.Error.Value);
            Assert.IsTrue(editor.IsOpen.Value);
            Assert.AreEqual(0, _sessions.Current.Client.PendingOperationCount);
        }

        [Test]
        public void Editor_CreatesAndEditsProfile_AndEverythingRefreshes()
        {
            var editor = _main.Profiles.Editor;
            _main.Profiles.CreateCommand.Execute();
            editor.Nickname.Value = "neo";
            editor.Coins.Value = "120";
            editor.SaveCommand.Execute();

            Assert.IsFalse(editor.IsOpen.Value);
            var item = _main.Profiles.Items.Items.Single();
            Assert.AreEqual("neo", item.Nickname);
            Assert.AreEqual("120", item.Coins);
            Assert.IsFalse(_main.Profiles.IsEmpty.Value);
            Assert.AreEqual("1", _main.PendingBadge.Value);
            Assert.IsTrue(_main.HasPending.Value);
            Assert.AreEqual("Ожидают отправки: 1", _main.Journal.Summary.Value);
            StringAssert.Contains("Записано в журнал", _main.Toast.Value.Text);

            item.OpenCommand.Execute();
            Assert.AreEqual("Изменить профиль", editor.Title.Value);
            Assert.AreEqual("neo", editor.Nickname.Value);
            editor.Nickname.Value = "trinity";
            editor.SaveCommand.Execute();

            Assert.AreEqual("trinity", _main.Profiles.Items.Items.Single().Nickname);
            Assert.AreEqual(2, _main.Journal.Items.Count);
            CollectionAssert.Contains(_main.Journal.Items.Items[1].Changes, "nickname:  neo  →  trinity");
        }

        [Test]
        public void Sync_WhenOffline_ReportsErrorWithoutContactingServer()
        {
            _main.ToggleOnlineCommand.Execute();
            Assert.IsFalse(_main.Sync.IsOnline.Value);
            Assert.AreEqual("● Offline", _main.NetworkText.Value);

            var run = _main.Sync.SyncCommand.ExecuteAsync();

            Assert.IsTrue(run.IsCompleted, "Offline sync fails immediately.");
            Assert.AreEqual(StatusKind.Error, _main.Sync.Status.Value);
            StringAssert.Contains("offline", _main.Sync.StatusText.Value);
            Assert.IsTrue(_main.Toast.Value.IsError);
            Assert.IsFalse(_main.Sync.IsBusy.Value);
        }

        [Test]
        public void Settings_SwitchVersionAndResetDatabase()
        {
            _main.Profiles.CreateCommand.Execute();
            _main.Profiles.Editor.Nickname.Value = "neo";
            _main.Profiles.Editor.SaveCommand.Execute();

            var v3 = _main.Settings.Versions.Single(v => v.Version == SchemaVersions.V3);
            v3.SelectCommand.Execute();

            Assert.AreEqual(SchemaVersions.V3, _sessions.SchemaVersion);
            Assert.IsTrue(v3.IsSelected.Value);
            Assert.AreEqual("unity-v3 · схема v3", _main.ClientLabel.Value);
            StringAssert.Contains("region", _main.Settings.ClientInfo.Value);
            Assert.IsTrue(_main.Profiles.IsEmpty.Value, "v3 has its own local database.");

            _main.Settings.Versions.Single(v => v.Version == SchemaVersions.V1).SelectCommand.Execute();
            Assert.AreEqual(1, _main.Profiles.Items.Count);

            _main.Settings.ResetDatabaseCommand.Execute();
            Assert.IsTrue(_main.Profiles.IsEmpty.Value);
            Assert.IsFalse(_main.HasPending.Value);
        }

        [Test]
        public void Settings_ShowsServerStatusAndNormalizesUrl()
        {
            Assert.AreEqual("Сервер ещё не проверялся", _main.Settings.ServerHealth.Value);

            _main.Settings.ServerUrl.Value = "127.0.0.1:9";
            _main.Settings.ApplyServerUrlCommand.Execute();

            Assert.AreEqual("http://127.0.0.1:9/", _main.Settings.ServerUrl.Value);
        }

        [Test]
        public void Editor_V1ClientSeesOnlyKnownFields()
        {
            var editor = _main.Profiles.Editor;
            _main.Profiles.CreateCommand.Execute();

            Assert.IsFalse(editor.ShowV2Fields.Value);
            Assert.IsFalse(editor.ShowRegion.Value);
            Assert.IsFalse(editor.IsEditing.Value);
            StringAssert.Contains("не знает полей", editor.Hint.Value);

            editor.Nickname.Value = "neo";
            editor.Coins.Value = "100";
            editor.SaveCommand.Execute();

            var insert = _sessions.Current.Client.PendingOperations().Single();
            CollectionAssert.AreEquivalent(new[] { "nickname", "coins" }, insert.Changes.Select(c => c.Field).ToArray());
        }

        [Test]
        public void Editor_V3ClientEditsNewVersionFieldsAndDeletesProfile()
        {
            _main.Settings.Versions.Single(v => v.Version == SchemaVersions.V3).SelectCommand.Execute();
            var editor = _main.Profiles.Editor;
            _main.Profiles.CreateCommand.Execute();
            Assert.IsTrue(editor.ShowV2Fields.Value);
            Assert.IsTrue(editor.ShowRegion.Value);

            editor.Nickname.Value = "neo";
            editor.Coins.Value = "100";
            editor.Avatar.Value = "a1";
            editor.Premium.Value = "много";
            editor.SaveCommand.Execute();
            Assert.AreEqual("Премиальная валюта — целое число", editor.Error.Value);

            editor.Premium.Value = "5";
            editor.Region.Value = " eu ";
            editor.SaveCommand.Execute();
            Assert.IsFalse(editor.IsOpen.Value);

            var profile = _main.Profiles.Items.Items.Single();
            CollectionAssert.Contains(profile.Fields, new System.Collections.Generic.KeyValuePair<string, string>("region", "eu"));

            profile.OpenCommand.Execute();
            Assert.IsTrue(editor.IsEditing.Value);
            Assert.AreEqual("a1", editor.Avatar.Value);
            Assert.AreEqual("5", editor.Premium.Value);
            Assert.AreEqual("eu", editor.Region.Value);

            editor.Region.Value = "us";
            editor.SaveCommand.Execute();
            var update = _sessions.Current.Client.PendingOperations().Last();
            Assert.AreEqual(Sync.Core.Protocol.OperationType.Update, update.Type);
            Assert.AreEqual("region", update.Changes.Single().Field);
            Assert.AreEqual("us", update.Changes.Single().NewValue);

            _main.Profiles.Items.Items.Single().OpenCommand.Execute();
            editor.DeleteCommand.Execute();
            Assert.AreEqual(Sync.Core.Protocol.OperationType.Delete, _sessions.Current.Client.PendingOperations().Last().Type);
            Assert.IsTrue(_main.Profiles.IsEmpty.Value);
        }

        [Test]
        public void Settings_StrategyChoiceFollowsServerStatus()
        {
            Assert.IsFalse(_main.Settings.CanSwitchStrategy.Value);

            _server.Apply(new ServerHealth(3, "Lww", "resolver", true, new[] { "ServerWins", "Lww", "ClientWins", "FieldLwwNoSchema", "Adaptive" }));
            _main.Invalidate();

            var options = _main.Settings.Strategies.Items;
            Assert.IsTrue(_main.Settings.CanSwitchStrategy.Value);
            CollectionAssert.AreEqual(new[] { "Adaptive", "ServerWins", "Lww", "ClientWins", "FieldLwwNoSchema" }, options.Select(o => o.Name).ToArray());
            Assert.AreEqual("LWW", options.Single(o => o.IsSelected).Label);
            Assert.AreEqual("Сервер доступен · схема сервера v3, клиента v1 · стратегия LWW (сервис resolver)",
                _main.Settings.ServerHealth.Value);

            _server.Apply(new ServerHealth(3, "Adaptive", "builtin"));
            _main.Invalidate();
            Assert.IsFalse(_main.Settings.CanSwitchStrategy.Value);
        }

        [UnityTest]
        public IEnumerator Settings_CheckServer_KeepsStatusTextAndReportsResult()
        {
            _main.Invalidate();
            var before = _main.Settings.ServerHealth.Value;

            var run = _main.Settings.CheckServerCommand.ExecuteAsync();
            Assert.AreEqual(before, _main.Settings.ServerHealth.Value, "The status card must not change size while the check runs.");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!run.IsCompleted && DateTime.UtcNow < deadline)
                yield return null;

            Assert.IsTrue(run.IsCompleted);
            StringAssert.StartsWith("Нет соединения", _main.Settings.ServerHealth.Value);
            Assert.IsTrue(_main.Toast.Value.IsError);
            StringAssert.StartsWith("Нет соединения", _main.Toast.Value.Text);
        }

        [Test]
        public void Display_ExplainsSituationsAndFieldOutcomes()
        {
            StringAssert.Contains("tombstone", Display.Situation(Sync.Core.Strategies.SyncSituation.S5));
            StringAssert.Contains("сохранены", Display.Situation(Sync.Core.Strategies.SyncSituation.S3));
            Assert.AreEqual("затёрто значением по умолчанию", Display.FieldOutcomeText(Sync.Core.Strategies.FieldOutcome.Overwritten));
            Assert.AreEqual("Адаптивный", Display.StrategyLabel("Adaptive"));
        }

        [Test]
        public void Preloader_ShowsProgressAndRetriesAfterError()
        {
            var retries = 0;
            var preloader = new PreloaderViewModel(() => retries++);

            preloader.Apply(new BootProgress(2, 4, "Проверка сервера синхронизации", 0.375f));
            Assert.AreEqual("Проверка сервера синхронизации  ·  3/4", preloader.StepText.Value);
            Assert.AreEqual("38%", preloader.PercentText.Value);
            Assert.AreEqual(0.375f, preloader.Progress.Value);

            preloader.ShowError("Открытие локальной базы", new InvalidOperationException("locked"));
            Assert.IsTrue(preloader.HasError.Value);
            StringAssert.Contains("locked", preloader.ErrorText.Value);

            preloader.RetryCommand.Execute();
            Assert.AreEqual(1, retries);
            Assert.IsFalse(preloader.HasError.Value);
        }
    }
}
