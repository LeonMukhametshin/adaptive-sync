#nullable enable
using System;
using System.Collections.Generic;

namespace AdaptiveSync.Demo.App
{
    public sealed class AppServices : IDisposable
    {
        private readonly Dictionary<Type, object> _services = new Dictionary<Type, object>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private bool _disposed;

        public void Register<T>(T service) where T : class
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AppServices));
            if (service == null) throw new ArgumentNullException(nameof(service));

            if (_services.TryGetValue(typeof(T), out var previous) && previous is IDisposable old)
            {
                _disposables.Remove(old);
                old.Dispose();
            }

            _services[typeof(T)] = service;
            if (service is IDisposable disposable) _disposables.Add(disposable);
        }

        public bool TryGet<T>(out T service) where T : class
        {
            if (_services.TryGetValue(typeof(T), out var value))
            {
                service = (T)value;
                return true;
            }

            service = null!;
            return false;
        }

        public T Get<T>() where T : class =>
            TryGet<T>(out var service)
                ? service
                : throw new InvalidOperationException(
                    "Service " + typeof(T).Name + " is not registered. The app must start from the Boot scene.");

        public bool Has<T>() where T : class => _services.ContainsKey(typeof(T));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (var i = _disposables.Count - 1; i >= 0; i--)
                _disposables[i].Dispose();
            _disposables.Clear();
            _services.Clear();
        }
    }
}
