using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdaptiveSync.Demo.App;
using AdaptiveSync.Demo.Boot;
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace AdaptiveSync.Demo.EditModeTests
{
    public sealed class PreloaderTests
    {
        [Test]
        public void RunsStepsInOrderAndRegistersServices()
        {
            var log = new List<string>();
            var services = new AppServices();
            var preloader = new Preloader(new IBootStep[]
            {
                new FakeStep("a", 1f, log, s => s.Register(new Marker("a"))),
                new FakeStep("b", 1f, log, s => Assert.AreEqual("a", s.Get<Marker>().Name)),
                new FakeStep("c", 1f, log),
            });

            var result = Run(preloader, services);

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(preloader.IsComplete);
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, log);
        }

        [Test]
        public void ReportsOverallProgressByWeight()
        {
            var reports = new List<BootProgress>();
            var preloader = new Preloader(new IBootStep[]
            {
                new FakeStep("light", 1f, new List<string>(), report: 0.5f),
                new FakeStep("heavy", 3f, new List<string>(), report: 0.5f),
            });
            preloader.ProgressChanged += reports.Add;

            Run(preloader, new AppServices());

            var overall = reports.Select(r => r.Overall).ToList();
            CollectionAssert.AreEqual(new[] { 0f, 0.125f, 0.25f, 0.25f, 0.625f, 1f }, overall);
            Assert.AreEqual("heavy", reports.Last().Title);
            Assert.AreEqual(2, reports.Last().StepCount);
            CollectionAssert.AreEqual(overall.OrderBy(v => v), overall, "Progress must never go backwards.");
        }

        [Test]
        public void StopsOnFailingStepAndRetriesFromIt()
        {
            var log = new List<string>();
            var failures = 1;
            var flaky = new FakeStep("flaky", 1f, log, _ =>
            {
                if (failures-- > 0) throw new InvalidOperationException("storage is locked");
            });
            var preloader = new Preloader(new IBootStep[] { new FakeStep("first", 1f, log), flaky, new FakeStep("last", 1f, log) });
            var services = new AppServices();

            var failed = Run(preloader, services);

            Assert.IsFalse(failed.Succeeded);
            Assert.AreSame(flaky, failed.FailedStep);
            StringAssert.Contains("storage is locked", failed.Error.Message);
            Assert.AreEqual(1, preloader.CompletedSteps);
            CollectionAssert.AreEqual(new[] { "first", "flaky" }, log);

            var retried = Run(preloader, services);

            Assert.IsTrue(retried.Succeeded);
            CollectionAssert.AreEqual(new[] { "first", "flaky", "flaky", "last" }, log, "Retry must resume from the failed step.");
        }

        [Test]
        public void CancellationIsNotReportedAsStepFailure()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var preloader = new Preloader(new IBootStep[] { new FakeStep("a", 1f, new List<string>()) });

            Assert.Catch<OperationCanceledException>(() =>
                preloader.RunAsync(new AppServices(), cancellation.Token).GetAwaiter().GetResult());
        }

        [Test]
        public void RejectsEmptyOrZeroWeightSteps()
        {
            Assert.Throws<ArgumentException>(() => new Preloader(Array.Empty<IBootStep>()));
            Assert.Throws<ArgumentException>(() => new Preloader(new IBootStep[] { new FakeStep("zero", 0f, new List<string>()) }));
        }

        [Test]
        public void AppEntryPointDeclaresBootStepsInOrder()
        {
            CollectionAssert.AreEqual(
                new[] { "LoadSettingsStep", "OpenStorageStep", "CheckServerStep", "LoadSceneStep" },
                AppEntryPoint.CreateSteps().Select(s => (s is PacedStep paced ? paced.Inner : s).GetType().Name));
        }

        [Test]
        public void PacedStepWithoutMinimumPassesThroughSynchronously()
        {
            var reports = new List<float>();
            var log = new List<string>();
            var step = new PacedStep(new FakeStep("a", 2f, log, report: 0.5f), 0f, () => 0f);

            var task = step.RunAsync(new AppServices(), new ListProgress(reports), CancellationToken.None);

            Assert.IsTrue(task.IsCompleted);
            CollectionAssert.AreEqual(new[] { "a" }, log);
            CollectionAssert.AreEqual(new[] { 0.5f }, reports);
            Assert.AreEqual(2f, step.Weight);
            Assert.AreEqual("a", step.Title);
        }

        [UnityTest]
        public IEnumerator PacedStepWaitsMinimumTimeAndNeverRunsAheadOfTime()
        {
            var now = 0f;
            var reports = new List<float>();
            var step = new PacedStep(new FakeStep("fast", 1f, new List<string>(), report: 1f), 2f, () => now);

            var task = step.RunAsync(new AppServices(), new ListProgress(reports), CancellationToken.None);
            for (var frame = 0; frame < 3; frame++)
            {
                yield return null;
                Assert.IsFalse(task.IsCompleted, "The step must wait for its minimum duration.");
            }

            now = 1f;
            var reportDeadline = DateTime.UtcNow.AddSeconds(5);
            while (reports.Last() < 0.4999f && DateTime.UtcNow < reportDeadline) yield return null;
            Assert.IsFalse(task.IsCompleted);
            Assert.That(reports.Last(), Is.EqualTo(0.5f).Within(0.0001f));

            now = 2.5f;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) yield return null;

            Assert.IsTrue(task.IsCompleted);
            task.GetAwaiter().GetResult();
            Assert.That(reports.Max(), Is.LessThanOrEqualTo(1f));
            CollectionAssert.AreEqual(reports.OrderBy(v => v), reports, "Displayed progress must never go backwards.");
        }

        [UnityTest]
        public IEnumerator PacedStepFailureIsNotDelayed()
        {
            var step = new PacedStep(
                new FakeStep("broken", 1f, new List<string>(), _ => throw new InvalidOperationException("boom")), 100f, () => 0f);

            var task = step.RunAsync(new AppServices(), new ListProgress(new List<float>()), CancellationToken.None);
            yield return null;
            yield return null;

            Assert.IsTrue(task.IsFaulted);
            StringAssert.Contains("boom", task.Exception!.InnerException!.Message);
        }

        private static BootResult Run(Preloader preloader, AppServices services)
        {
            var task = preloader.RunAsync(services);
            Assert.IsTrue(task.IsCompleted, "Fake steps complete synchronously.");
            return task.GetAwaiter().GetResult();
        }

        private sealed class ListProgress : IProgress<float>
        {
            private readonly List<float> _values;

            public ListProgress(List<float> values)
            {
                _values = values;
            }

            public void Report(float value) => _values.Add(value);
        }

        private sealed class Marker
        {
            public Marker(string name) => Name = name;

            public string Name { get; }
        }

        private sealed class FakeStep : IBootStep
        {
            private readonly List<string> _log;
            private readonly Action<AppServices> _action;
            private readonly float? _report;

            public FakeStep(string title, float weight, List<string> log, Action<AppServices> action = null, float? report = null)
            {
                Title = title;
                Weight = weight;
                _log = log;
                _action = action ?? (_ => { });
                _report = report;
            }

            public string Title { get; }

            public float Weight { get; }

            public Task RunAsync(AppServices services, IProgress<float> progress, CancellationToken cancellationToken)
            {
                _log.Add(Title);
                if (_report.HasValue) progress.Report(_report.Value);
                _action(services);
                return Task.CompletedTask;
            }
        }
    }
}
