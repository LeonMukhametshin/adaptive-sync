#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdaptiveSync.Demo.App;

namespace AdaptiveSync.Demo.Boot
{
    public readonly struct BootProgress
    {
        public BootProgress(int stepIndex, int stepCount, string title, float overall)
        {
            StepIndex = stepIndex;
            StepCount = stepCount;
            Title = title;
            Overall = overall;
        }

        public int StepIndex { get; }

        public int StepCount { get; }

        public string Title { get; }

        public float Overall { get; }
    }

    public sealed class BootResult
    {
        private BootResult(IBootStep? failedStep, Exception? error)
        {
            FailedStep = failedStep;
            Error = error;
        }

        public bool Succeeded => FailedStep == null;

        public IBootStep? FailedStep { get; }

        public Exception? Error { get; }

        public static BootResult Success() => new BootResult(null, null);

        public static BootResult Failure(IBootStep step, Exception error) => new BootResult(step, error);
    }

    public sealed class Preloader
    {
        private readonly IReadOnlyList<IBootStep> _steps;
        private readonly float _totalWeight;

        public Preloader(IEnumerable<IBootStep> steps)
        {
            _steps = steps.ToList();
            if (_steps.Count == 0) throw new ArgumentException("Preloader needs at least one step.", nameof(steps));
            if (_steps.Any(s => s.Weight <= 0)) throw new ArgumentException("Step weights must be positive.", nameof(steps));
            _totalWeight = _steps.Sum(s => s.Weight);
        }

        public event Action<BootProgress>? ProgressChanged;

        public IReadOnlyList<IBootStep> Steps => _steps;

        public int CompletedSteps { get; private set; }

        public bool IsComplete => CompletedSteps == _steps.Count;

        public async Task<BootResult> RunAsync(AppServices services, CancellationToken cancellationToken = default)
        {
            while (CompletedSteps < _steps.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = CompletedSteps;
                var step = _steps[index];
                var progress = new StepProgress(this, index);
                progress.Report(0f);

                try
                {
                    await step.RunAsync(services, progress, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return BootResult.Failure(step, ex);
                }

                progress.Report(1f);
                CompletedSteps++;
            }

            return BootResult.Success();
        }

        private void Report(int index, float stepProgress)
        {
            var done = _steps.Take(index).Sum(s => s.Weight);
            var clamped = Math.Max(0f, Math.Min(1f, stepProgress));
            var overall = (done + _steps[index].Weight * clamped) / _totalWeight;
            ProgressChanged?.Invoke(new BootProgress(index, _steps.Count, _steps[index].Title, overall));
        }

        private sealed class StepProgress : IProgress<float>
        {
            private readonly Preloader _owner;
            private readonly int _index;

            public StepProgress(Preloader owner, int index)
            {
                _owner = owner;
                _index = index;
            }

            public void Report(float value) => _owner.Report(_index, value);
        }
    }
}
