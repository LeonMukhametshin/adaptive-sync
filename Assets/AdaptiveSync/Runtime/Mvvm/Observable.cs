#nullable enable
using System;
using System.Collections.Generic;

namespace AdaptiveSync.Demo.Mvvm
{
    public interface IReadOnlyObservable<out T>
    {
        T Value { get; }

        IDisposable Subscribe(Action<T> handler, bool invokeNow = true);
    }

    public sealed class Observable<T> : IReadOnlyObservable<T>
    {
        private readonly List<Action<T>> _handlers = new List<Action<T>>();
        private T _value;

        public Observable(T initial = default!)
        {
            _value = initial;
        }

        public T Value
        {
            get => _value;
            set
            {
                if (EqualityComparer<T>.Default.Equals(_value, value)) return;
                _value = value;
                foreach (var handler in _handlers.ToArray())
                    handler(value);
            }
        }

        public IDisposable Subscribe(Action<T> handler, bool invokeNow = true)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers.Add(handler);
            if (invokeNow) handler(_value);
            return new Subscription(() => _handlers.Remove(handler));
        }

        public override string ToString() => _value?.ToString() ?? "";
    }

    public sealed class ObservableList<T>
    {
        private readonly List<Action<IReadOnlyList<T>>> _handlers = new List<Action<IReadOnlyList<T>>>();
        private IReadOnlyList<T> _items = Array.Empty<T>();

        public IReadOnlyList<T> Items => _items;

        public int Count => _items.Count;

        public void Replace(IEnumerable<T> items)
        {
            _items = new List<T>(items);
            foreach (var handler in _handlers.ToArray())
                handler(_items);
        }

        public IDisposable Subscribe(Action<IReadOnlyList<T>> handler, bool invokeNow = true)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers.Add(handler);
            if (invokeNow) handler(_items);
            return new Subscription(() => _handlers.Remove(handler));
        }
    }

    public sealed class Subscription : IDisposable
    {
        private Action? _dispose;

        public Subscription(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            _dispose?.Invoke();
            _dispose = null;
        }
    }

    public sealed class CompositeDisposable : IDisposable
    {
        private readonly List<IDisposable> _items = new List<IDisposable>();
        private bool _disposed;

        public T Add<T>(T item) where T : IDisposable
        {
            if (_disposed) item.Dispose();
            else _items.Add(item);
            return item;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (var i = _items.Count - 1; i >= 0; i--)
                _items[i].Dispose();
            _items.Clear();
        }
    }
}
