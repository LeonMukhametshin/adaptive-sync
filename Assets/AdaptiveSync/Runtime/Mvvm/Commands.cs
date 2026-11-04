#nullable enable
using System;
using System.Threading.Tasks;

namespace AdaptiveSync.Demo.Mvvm
{
    public interface ICommand
    {
        IReadOnlyObservable<bool> CanExecute { get; }

        void Execute();
    }

    public sealed class Command : ICommand
    {
        private readonly Action _execute;

        public Command(Action execute, IReadOnlyObservable<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            CanExecute = canExecute ?? new Observable<bool>(true);
        }

        public IReadOnlyObservable<bool> CanExecute { get; }

        public void Execute()
        {
            if (CanExecute.Value) _execute();
        }
    }

    public sealed class Command<T>
    {
        private readonly Action<T> _execute;

        public Command(Action<T> execute)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        }

        public void Execute(T parameter) => _execute(parameter);
    }

    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Observable<bool> _isRunning = new Observable<bool>(false);
        private readonly Observable<bool> _canExecute = new Observable<bool>(true);
        private readonly IReadOnlyObservable<bool>? _condition;

        public AsyncCommand(Func<Task> execute, IReadOnlyObservable<bool>? condition = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _condition = condition;
            _isRunning.Subscribe(_ => UpdateCanExecute(), invokeNow: false);
            _condition?.Subscribe(_ => UpdateCanExecute(), invokeNow: false);
            UpdateCanExecute();
        }

        public IReadOnlyObservable<bool> IsRunning => _isRunning;

        public IReadOnlyObservable<bool> CanExecute => _canExecute;

        public Task? LastRun { get; private set; }

        public void Execute()
        {
            if (!_canExecute.Value) return;
            LastRun = RunAsync();
        }

        public Task ExecuteAsync()
        {
            Execute();
            return LastRun ?? Task.CompletedTask;
        }

        private async Task RunAsync()
        {
            _isRunning.Value = true;
            try
            {
                await _execute();
            }
            finally
            {
                _isRunning.Value = false;
            }
        }

        private void UpdateCanExecute() => _canExecute.Value = !_isRunning.Value && (_condition?.Value ?? true);
    }
}
