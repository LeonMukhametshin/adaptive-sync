using System.Collections.Generic;
using System.Linq;
using AdaptiveSync.Demo.Mvvm;
using AdaptiveSync.Demo.Services;

namespace AdaptiveSync.Demo.ViewModels
{
    public sealed class OperationItem
    {
        public OperationItem(string title, string type, string details, IReadOnlyList<string> changes)
        {
            Title = title;
            Type = type;
            Details = details;
            Changes = changes;
        }

        public string Title { get; }

        public string Type { get; }

        public string TypeClass => "badge--" + Type.ToLowerInvariant();

        public string Details { get; }

        public IReadOnlyList<string> Changes { get; }
    }

    public sealed class JournalViewModel : ViewModelBase
    {
        private readonly SessionService _sessions;

        public JournalViewModel(SessionService sessions)
        {
            _sessions = sessions;
        }

        public Observable<string> Summary { get; } = new Observable<string>("");

        public Observable<bool> IsEmpty { get; } = new Observable<bool>(true);

        public ObservableList<OperationItem> Items { get; } = new ObservableList<OperationItem>();

        public void Refresh()
        {
            var operations = _sessions.Current.PendingOperations;
            Summary.Value = "Ожидают отправки: " + operations.Count;
            IsEmpty.Value = operations.Count == 0;
            Items.Replace(operations.Select(o => new OperationItem(
                o.Table + " · " + Display.Short(o.RecordId),
                o.Type.ToString(),
                "логическое время " + o.LogicalTime + " · base row_version " + o.BaseRowVersion,
                o.Changes.Select(c => c.Field + ":  " + Display.Value(c.OldValue) + "  →  " + Display.Value(c.NewValue)).ToList())));
        }
    }
}
