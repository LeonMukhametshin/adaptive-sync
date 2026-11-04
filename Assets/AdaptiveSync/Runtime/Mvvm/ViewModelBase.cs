using System;

namespace AdaptiveSync.Demo.Mvvm
{
    public abstract class ViewModelBase : IDisposable
    {
        protected CompositeDisposable Disposables { get; } = new CompositeDisposable();

        public virtual void Dispose() => Disposables.Dispose();
    }
}
