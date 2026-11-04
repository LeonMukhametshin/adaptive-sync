#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AdaptiveSync.Demo.App;
using AdaptiveSync.Demo.Services;
using AdaptiveSync.Demo.UI;
using AdaptiveSync.Demo.Views;
using NUnit.Framework;
using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Time;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace AdaptiveSync.Demo.Tests
{
    public sealed class AppPlayModeTests
    {
        private static string ServerUrl => Environment.GetEnvironmentVariable("ADAPTIVE_SYNC_SERVER") ?? "http://localhost:5080/";

        private string _directory = "";

        [SetUp]
        public void SetUp()
        {
            AppEntryPoint.PaceOverride = 0f;
            _directory = Path.Combine(Application.temporaryCachePath, "adaptive-sync-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            AppEntryPoint.Shutdown();
            AppEntryPoint.PaceOverride = null;
            try
            {
                Directory.Delete(_directory, true);
            }
            catch (IOException)
            {
            }
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator V1AndV3Clients_OfflineEditsAndSync_MatchIntegrationTest()
        {
            var task = RunScenarioAsync();
            yield return new WaitUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }

        [UnityTest]
        public IEnumerator MainScreen_BuildsInterface_AndSyncHandlerReportsResult()
        {
            var demo = default(MainView);
            yield return LoadDemo(d => demo = d);

            foreach (var name in new[] { "app", "profile-list", "sync-button", "journal-list", "server-url", "tabbar", "sheet-overlay" })
                Assert.IsNotNull(demo!.Root.Q(name), "Missing UI element " + name);

            var main = demo!;
            GoOffline(main);
            main.SelectTab("sync");
            Assert.IsTrue(main.Root.Q("page-sync").ClassListContains("page--active"));
            Assert.AreEqual("● Offline", main.Root.Q<Button>("network-pill").text);

            var task = main.SynchronizeAsync();
            yield return new WaitUntil(() => task.IsCompleted);

            StringAssert.Contains("offline", main.Session!.LastError);
            StringAssert.Contains("offline", main.Root.Q<Label>("sync-status").text);
        }

        [UnityTest]
        public IEnumerator SyncButtonTap_StartsSync()
        {
            Assume.That(!Application.isBatchMode, "UI Toolkit does not lay out panels in batch mode; run this test from the Test Runner window.");

            var demo = default(MainView);
            yield return LoadDemo(d => demo = d);
            var main = demo!;
            GoOffline(main);
            main.SelectTab("sync");
            yield return null;
            yield return null;

            Tap(main.Root.Q<Button>("sync-button"));

            yield return new WaitUntil(() => !main.IsBusy && main.Session!.LastError != null);
            StringAssert.Contains("offline", main.Root.Q<Label>("sync-status").text);
        }

        [UnityTest]
        public IEnumerator PageScroll_MouseDrag_ScrollsContent()
        {
            Assume.That(!Application.isBatchMode, "UI Toolkit does not lay out panels in batch mode; run this test from the editor.");

            var demo = default(MainView);
            yield return LoadDemo(d => demo = d);
            demo!.SelectTab("settings");
            var scroll = demo.Root.Q<ScrollView>("page-settings");
            scroll.Add(new VisualElement { style = { height = 3000f } });
            yield return null;
            yield return null;

            Assert.AreEqual(ScrollView.TouchScrollBehavior.Elastic, scroll.touchScrollBehavior);
            Assert.Greater(scroll.verticalScroller.highValue, 0f, "Page content must overflow the viewport for this test.");

            var target = scroll.Q<Label>("server-health");
            Drag(target, -150f);
            yield return null;

            Assert.Greater(scroll.scrollOffset.y, 0f, "Dragging the page up must scroll it down.");
            Assert.IsFalse(demo.Root.Q("sheet-overlay").ClassListContains("overlay--open"), "A drag must not be treated as a tap.");

            var max = MouseDragScroller.MaxOffset(scroll);
            Drag(target, -100000f);
            yield return null;
            Assert.That(scroll.scrollOffset.y, Is.EqualTo(max).Within(0.5f), "Scrolling must stop at the end of the content.");

            Drag(target, 100000f);
            yield return null;
            Assert.That(scroll.scrollOffset.y, Is.EqualTo(0f).Within(0.5f), "Scrolling must stop at the top of the content.");
        }

        [UnityTest]
        public IEnumerator LongPage_DoesNotSqueezeHeaderAndTabbar()
        {
            Assume.That(!Application.isBatchMode, "UI Toolkit does not lay out panels in batch mode; run this test from the editor.");

            var demo = default(MainView);
            yield return LoadDemo(d => demo = d);
            var page = demo!.Root.Q<ScrollView>("page-profiles");
            page.Add(new VisualElement { style = { height = 10000f } });
            yield return null;
            yield return null;

            var header = demo.Root.Q("header");
            var title = header.Q<Label>(className: "header__title");
            var pill = demo.Root.Q("network-pill");
            var tabbar = demo.Root.Q("tabbar");
            var create = demo.Root.Q("create-profile");

            Assert.Greater(title.layout.height, 10f, "Header title must keep its height.");
            Assert.Greater(demo.Root.Q<Label>("client-label").layout.height, 5f, "Header subtitle must keep its height.");
            Assert.GreaterOrEqual(pill.worldBound.yMin, header.worldBound.yMin, "The network pill must stay inside the header.");
            Assert.LessOrEqual(pill.worldBound.yMax, header.worldBound.yMax + 0.5f, "The network pill must stay inside the header.");
            Assert.GreaterOrEqual(tabbar.layout.height, 59f, "Tab bar must keep its full height.");
            Assert.GreaterOrEqual(page.worldBound.yMin, header.worldBound.yMax - 0.5f, "Page content must start below the header.");
            Assert.LessOrEqual(page.worldBound.yMax, tabbar.worldBound.yMin + 0.5f, "Page content must end above the tab bar.");
            Assert.Greater(create.worldBound.xMin, 8f, "Page content needs side padding.");
        }

        [UnityTest]
        public IEnumerator PageScroll_ShortPage_DoesNotMove()
        {
            Assume.That(!Application.isBatchMode, "UI Toolkit does not lay out panels in batch mode; run this test from the editor.");

            var demo = default(MainView);
            yield return LoadDemo(d => demo = d);
            demo!.SelectTab("settings");
            var scroll = demo.Root.Q<ScrollView>("page-settings");
            foreach (var child in scroll.contentContainer.Children().ToList())
                child.style.display = DisplayStyle.None;
            scroll.Add(new VisualElement { name = "probe", style = { height = 20f } });
            yield return null;
            yield return null;

            Assert.AreEqual(0f, MouseDragScroller.MaxOffset(scroll), "Probe content must fit the viewport.");
            var probe = scroll.Q("probe");
            Drag(probe, -5000f);
            yield return null;
            Assert.AreEqual(0f, scroll.scrollOffset.y, "A page shorter than the screen must not scroll.");
            Drag(probe, 5000f);
            yield return null;
            Assert.AreEqual(0f, scroll.scrollOffset.y, "A page shorter than the screen must not scroll.");
        }

        private static void GoOffline(MainView demo)
        {
            if (demo.ViewModel!.IsOnline.Value) demo.ViewModel.ToggleOnlineCommand.Execute();
            Assert.IsFalse(demo.Session!.IsOnline);
        }

        private static void Drag(VisualElement target, float distance)
        {
            var start = target.worldBound.center;
            Send(target, PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, mousePosition = start, button = 0 }));
            for (var step = 1; step <= 6; step++)
            {
                var position = start + new Vector2(0f, distance * step / 6f);
                Send(target, PointerMoveEvent.GetPooled(new Event { type = EventType.MouseDrag, mousePosition = position, button = 0 }));
            }
            Send(target, PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, mousePosition = start + new Vector2(0f, distance), button = 0 }));
        }

        private static void Send(VisualElement target, EventBase e)
        {
            using (e)
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        [UnityTest]
        public IEnumerator Boot_RunsAllStepsAndOpensMainWithServices()
        {
            var demo = default(MainView);
            yield return LoadDemo(d => demo = d);

            var entry = AppEntryPoint.Instance!;
            Assert.IsTrue(entry.IsReady);
            Assert.IsTrue(entry.LastResult!.Succeeded);
            Assert.AreEqual(4, entry.Preloader.CompletedSteps);
            Assert.AreEqual(SceneNames.Main, SceneManager.GetActiveScene().name);
            var services = AppEntryPoint.Services!;
            Assert.IsTrue(services.Has<AppSettings>());
            Assert.IsTrue(services.Has<SessionService>());
            Assert.IsTrue(services.Has<ServerStatus>());
            Assert.AreSame(services.Get<SessionService>().Current, demo!.Session);
        }

        [UnityTest]
        public IEnumerator Boot_WithDefaultPace_ShowsPreloaderForConfiguredTime()
        {
            AppEntryPoint.PaceOverride = null;
            var minimum = AppEntryPoint.SettingsSeconds + AppEntryPoint.StorageSeconds + AppEntryPoint.ServerSeconds + AppEntryPoint.SceneSeconds;
            var started = Time.realtimeSinceStartup;
            yield return SceneManager.LoadSceneAsync(SceneNames.Boot);

            var progress = new List<float>();
            AppEntryPoint.Instance!.Preloader.ProgressChanged += p => progress.Add(p.Overall);
            Assert.IsNotNull(UnityEngine.Object.FindAnyObjectByType<PreloaderView>(), "Preloader must be on screen while booting.");

            var demo = default(MainView);
            yield return WaitForMain(d => demo = d);
            var elapsed = Time.realtimeSinceStartup - started;

            Assert.GreaterOrEqual(elapsed, minimum * 0.9f, "Boot must last at least the configured time.");
            Assert.Less(elapsed, minimum + 10f);
            Assert.Greater(progress.Count, 20, "Progress must be animated over many frames.");
            CollectionAssert.AreEqual(progress.OrderBy(v => v), progress, "Progress must never go backwards.");
            Assert.That(progress.Last(), Is.EqualTo(1f).Within(0.0001f));
        }

        [UnityTest]
        public IEnumerator Boot_WithUnreachableServer_StartsOffline()
        {
            var settings = AppSettings.Load();
            var originalUrl = settings.ServerUrl;
            settings.ServerUrl = "http://127.0.0.1:9/";
            settings.Save();
            try
            {
                var demo = default(MainView);
                yield return LoadDemo(d => demo = d);

                var status = AppEntryPoint.Services!.Get<ServerStatus>();
                Assert.IsTrue(status.IsChecked);
                Assert.IsFalse(status.IsReachable);
                StringAssert.Contains("сервер недоступен", demo!.Root.Q<Label>("client-label").text);
            }
            finally
            {
                settings.ServerUrl = originalUrl;
                settings.Save();
            }
        }

        [UnityTest]
        public IEnumerator MainScene_OpenedDirectly_RedirectsThroughBoot()
        {
            yield return SceneManager.LoadSceneAsync(SceneNames.Main);
            var demo = default(MainView);
            yield return WaitForMain(d => demo = d);

            Assert.IsNotNull(AppEntryPoint.Instance);
            Assert.IsTrue(AppEntryPoint.Instance!.IsReady);
            Assert.IsTrue(demo!.IsReady);
        }

        private static IEnumerator LoadDemo(Action<MainView> found)
        {
            yield return SceneManager.LoadSceneAsync(SceneNames.Boot);
            yield return WaitForMain(found);
        }

        private static IEnumerator WaitForMain(Action<MainView> found)
        {
            var deadline = Time.realtimeSinceStartup + 30f;
            MainView? demo = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                demo = UnityEngine.Object.FindAnyObjectByType<MainView>();
                if (demo != null && demo.IsReady && AppEntryPoint.Instance != null && AppEntryPoint.Instance.IsReady) break;
                if (AppEntryPoint.Instance != null && AppEntryPoint.Instance.LastResult != null && !AppEntryPoint.Instance.LastResult.Succeeded)
                    Assert.Fail("Boot failed at '" + AppEntryPoint.Instance.LastResult.FailedStep!.Title + "': " + AppEntryPoint.Instance.LastResult.Error);
                yield return null;
            }

            Assert.IsNotNull(demo, "Main scene with MainView was not opened.");
            Assert.IsTrue(demo!.IsReady, "MainView did not become ready.");
            Assert.IsNull(demo.FatalError, demo.FatalError);
            var document = demo.GetComponent<UIDocument>();
            Assert.IsNotNull(document.panelSettings, "UIDocument must have PanelSettings, otherwise nothing is drawn.");
            Assert.IsNotNull(document.panelSettings.themeStyleSheet, "PanelSettings must have a theme.");
            Assert.IsNotNull(demo.Session);
            found(demo);
        }

        private async Task RunScenarioAsync()
        {
            await RequireServerAsync();
            var strategy = (await SyncDemoSession.CheckHealthAsync(ServerUrl)).Strategy;
            var adaptive = strategy == "Adaptive";
            if (!adaptive && strategy != "ServerWins")
                Assert.Ignore("Scenario expectations exist for ServerWins and Adaptive, the server runs " + strategy + ".");

            var clock = new LogicalClock();
            var run = Guid.NewGuid().GetHashCode();
            using var v3 = new SyncDemoSession(ServerUrl, Path.Combine(_directory, "v3.db"), "unity-test-v3-" + run, SchemaVersions.V3, clock,
                DeterministicGuids.FromSeed(run));
            using var v1 = new SyncDemoSession(ServerUrl, Path.Combine(_directory, "v1.db"), "unity-test-v1-" + run, SchemaVersions.V1, clock,
                DeterministicGuids.FromSeed(run + 1));

            var profile = v3.Client.Insert(SchemaVersions.PlayerProfile, new Dictionary<string, object?>
            {
                ["nickname"] = "neo", ["coins"] = 100L, ["avatar_id"] = "a0", ["region"] = "eu",
            });
            await SyncOrFail(v3);
            AssertOutcomes(v3, OperationOutcome.Applied);

            await SyncOrFail(v1);
            var seenByV1 = v1.Client.Find(SchemaVersions.PlayerProfile, profile)!;
            Assert.AreEqual("neo", seenByV1.Values["nickname"]);
            Assert.IsFalse(seenByV1.Values.ContainsKey("region"));

            v1.IsOnline = false;
            v3.IsOnline = false;

            v3.Client.Update(SchemaVersions.PlayerProfile, profile, new Dictionary<string, object?> { ["region"] = "us", ["avatar_id"] = "a1" });
            v1.EditProfile(profile, "neo_v1", 150);

            Assert.IsFalse(await v1.SyncAsync());
            StringAssert.Contains("offline", v1.LastError);
            Assert.AreEqual(1, v1.Client.PendingOperationCount);

            v3.IsOnline = true;
            await SyncOrFail(v3);
            AssertOutcomes(v3, OperationOutcome.Applied);

            v1.IsOnline = true;
            await SyncOrFail(v1);
            AssertOutcomes(v1, adaptive ? OperationOutcome.Applied : OperationOutcome.Rejected);
            if (!adaptive)
                StringAssert.Contains("Conflict", v1.LastResponse!.Results[0].Message);

            await SyncOrFail(v3);

            var (nickname, coins) = adaptive ? ("neo_v1", 150L) : ("neo", 100L);
            var localV1 = v1.Client.Find(SchemaVersions.PlayerProfile, profile)!;
            var localV3 = v3.Client.Find(SchemaVersions.PlayerProfile, profile)!;
            Assert.AreEqual(nickname, localV1.Values["nickname"]);
            Assert.AreEqual(coins, localV1.Values["coins"]);
            Assert.AreEqual(nickname, localV3.Values["nickname"]);
            Assert.AreEqual(coins, localV3.Values["coins"]);
            Assert.AreEqual("us", localV3.Values["region"]);
            Assert.AreEqual("a1", localV3.Values["avatar_id"]);
            Assert.AreEqual(localV3.RowVersion, localV1.RowVersion);
            Assert.AreEqual(0, v1.Client.PendingOperationCount);
            Assert.AreEqual(0, v3.Client.PendingOperationCount);
        }

        private static void Tap(VisualElement element)
        {
            var position = element.worldBound.center;
            using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, mousePosition = position, button = 0, clickCount = 1 }))
            {
                down.target = element;
                element.SendEvent(down);
            }
            using (var up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, mousePosition = position, button = 0, clickCount = 1 }))
            {
                up.target = element;
                element.SendEvent(up);
            }
        }

        private static async Task RequireServerAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                var response = await http.GetAsync(new Uri(new Uri(ServerUrl), "health"));
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
            }
            Assert.Ignore("Sync server is not running at " + ServerUrl + "; start it with docker compose up in adaptive-sync.");
        }

        private static async Task SyncOrFail(SyncDemoSession session)
        {
            if (!await session.SyncAsync())
                Assert.Fail(session.Client.ClientId + " failed to sync: " + session.LastError);
        }

        private static void AssertOutcomes(SyncDemoSession session, params OperationOutcome[] expected) =>
            CollectionAssert.AreEqual(expected, session.LastResponse!.Results.Select(r => r.Outcome).ToArray());
    }
}
