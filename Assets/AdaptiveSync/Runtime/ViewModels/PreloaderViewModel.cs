#nullable enable
using System;
using AdaptiveSync.Demo.Boot;
using AdaptiveSync.Demo.Mvvm;

namespace AdaptiveSync.Demo.ViewModels
{
    public sealed class PreloaderViewModel : ViewModelBase
    {
        public PreloaderViewModel(Action retry)
        {
            RetryCommand = new Command(() =>
            {
                HasError.Value = false;
                retry();
            });
        }

        public Observable<string> StepText { get; } = new Observable<string>("Подготовка...");

        public Observable<float> Progress { get; } = new Observable<float>(0f);

        public Observable<string> PercentText { get; } = new Observable<string>("0%");

        public Observable<bool> HasError { get; } = new Observable<bool>(false);

        public Observable<string> ErrorText { get; } = new Observable<string>("");

        public ICommand RetryCommand { get; }

        public void Apply(BootProgress progress)
        {
            StepText.Value = progress.Title + "  ·  " + (progress.StepIndex + 1) + "/" + progress.StepCount;
            Progress.Value = progress.Overall;
            PercentText.Value = (int)Math.Round(progress.Overall * 100f) + "%";
        }

        public void ShowRunning() => HasError.Value = false;

        public void ShowError(string stepTitle, Exception error)
        {
            ErrorText.Value = "Не удалось: " + stepTitle + "\n" + error.GetType().Name + ": " + error.Message;
            HasError.Value = true;
        }
    }
}
