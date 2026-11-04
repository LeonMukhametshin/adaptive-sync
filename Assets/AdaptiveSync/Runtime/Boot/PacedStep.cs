using System;
using System.Threading;
using System.Threading.Tasks;
using AdaptiveSync.Demo.App;

namespace AdaptiveSync.Demo.Boot
{
    public sealed class PacedStep : IBootStep
    {
        private readonly Func<float> _now;

        public PacedStep(IBootStep inner, float minimumSeconds, Func<float> now)
        {
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
            MinimumSeconds = Math.Max(0f, minimumSeconds);
            _now = now ?? throw new ArgumentNullException(nameof(now));
        }

        public IBootStep Inner { get; }

        public float MinimumSeconds { get; }

        public string Title => Inner.Title;

        public float Weight => Inner.Weight;

        public async Task RunAsync(AppServices services, IProgress<float> progress, CancellationToken cancellationToken)
        {
            var start = _now();
            var actual = 0f;
            var innerProgress = new CallbackProgress(value =>
            {
                actual = value;
                progress.Report(Displayed(actual, start));
            });

            var work = Inner.RunAsync(services, innerProgress, cancellationToken);
            while (!work.IsCompleted || (!work.IsFaulted && !work.IsCanceled && _now() - start < MinimumSeconds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(Displayed(work.IsCompleted ? 1f : actual, start));
                await Task.Yield();
            }

            await work;
        }

        private float Displayed(float actual, float start)
        {
            if (MinimumSeconds <= 0f) return actual;
            var byTime = (_now() - start) / MinimumSeconds;
            return Math.Min(actual, Math.Min(1f, byTime));
        }

        private sealed class CallbackProgress : IProgress<float>
        {
            private readonly Action<float> _report;

            public CallbackProgress(Action<float> report)
            {
                _report = report;
            }

            public void Report(float value) => _report(value);
        }
    }
}
