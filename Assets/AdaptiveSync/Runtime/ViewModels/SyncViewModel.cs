#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AdaptiveSync.Demo.Mvvm;
using AdaptiveSync.Demo.Services;
using Sync.Core.Protocol;

namespace AdaptiveSync.Demo.ViewModels
{
    public enum StatusKind
    {
        Neutral,
        Success,
        Error,
    }

    public sealed class ResultItem
    {
        public ResultItem(string title, string outcome, string details, string message)
            : this(title, outcome, details, message, "", Array.Empty<string>())
        {
        }

        public ResultItem(string title, string outcome, string details, string message, string situation, IReadOnlyList<string> fields)
        {
            Title = title;
            Outcome = outcome;
            Details = details;
            Message = message;
            Situation = situation;
            Fields = fields;
        }

        public string Situation { get; }

        public bool HasSituation => Situation.Length > 0;

        public IReadOnlyList<string> Fields { get; }

        public string Title { get; }

        public string Outcome { get; }

        public string OutcomeClass => "badge--" + Outcome.ToLowerInvariant();

        public string Details { get; }

        public string Message { get; }

        public bool HasMessage => Message.Length > 0;
    }

    public sealed class SyncViewModel : ViewModelBase
    {
        private readonly SessionService _sessions;
        private readonly IAppNotifier _notifier;
        private readonly AsyncCommand _sync;

        public SyncViewModel(SessionService sessions, IAppNotifier notifier)
        {
            _sessions = sessions;
            _notifier = notifier;
            _sync = new AsyncCommand(SynchronizeAsync);
            ToggleOnlineCommand = new Command(notifier.ToggleOnline);
            Disposables.Add(_sync.IsRunning.Subscribe(running =>
                SyncButtonText.Value = running ? "Синхронизация..." : "Синхронизировать"));
        }

        public AsyncCommand SyncCommand => _sync;

        public IReadOnlyObservable<bool> IsBusy => _sync.IsRunning;

        public ICommand ToggleOnlineCommand { get; }

        public Observable<bool> IsOnline { get; } = new Observable<bool>(true);

        public Observable<string> NetworkCaption { get; } = new Observable<string>("");

        public Observable<string> SyncButtonText { get; } = new Observable<string>("Синхронизировать");

        public Observable<string> StatusText { get; } = new Observable<string>("");

        public Observable<StatusKind> Status { get; } = new Observable<StatusKind>(StatusKind.Neutral);

        public Observable<string> PendingText { get; } = new Observable<string>("0");

        public Observable<string> CursorText { get; } = new Observable<string>("0");

        public Observable<string> RemoteText { get; } = new Observable<string>("—");

        public ObservableList<ResultItem> Results { get; } = new ObservableList<ResultItem>();

        public Observable<bool> ResultsEmpty { get; } = new Observable<bool>(true);

        public void Refresh()
        {
            var session = _sessions.Current;
            IsOnline.Value = session.IsOnline;
            NetworkCaption.Value = session.IsOnline ? "Синхронизация доступна" : "Офлайн: изменения копятся в журнале";
            PendingText.Value = Display.Number(session.Client.PendingOperationCount);
            CursorText.Value = Display.Number(session.Client.ServerVersion);

            var response = session.LastResponse;
            RemoteText.Value = response == null ? "—" : Display.Number(response.RemoteChanges.Count);
            Results.Replace(response == null
                ? Enumerable.Empty<ResultItem>()
                : response.Results.Select(r => new ResultItem(
                    "операция " + Display.Short(r.OpId),
                    r.Outcome.ToString(),
                    "row_version после обработки: " + r.RowVersion,
                    r.Message ?? "",
                    r.Situation.HasValue ? Display.Situation(r.Situation.Value) : "",
                    r.Fields
                        .Select(f => f.Field + " — " + Display.FieldOutcomeText(f.Outcome) +
                                     (f.Situation.HasValue ? " (" + f.Situation.Value + ")" : ""))
                        .ToList())));
            ResultsEmpty.Value = Results.Count == 0;
        }

        private async Task SynchronizeAsync()
        {
            var session = _sessions.Current;
            SetStatus("Синхронизация...", StatusKind.Neutral);
            _notifier.Invalidate();
            try
            {
                if (await session.SyncAsync())
                {
                    var response = session.LastResponse!;
                    var rejected = response.Results.Count(r => r.Outcome == OperationOutcome.Rejected);
                    SetStatus(rejected == 0
                            ? "Готово: операций " + response.Results.Count + ", изменений от других " + response.RemoteChanges.Count
                            : "Готово, отклонено операций: " + rejected,
                        rejected == 0 ? StatusKind.Success : StatusKind.Error);
                }
                else
                {
                    var error = session.LastError ?? "Ошибка синхронизации";
                    SetStatus(error, StatusKind.Error);
                    _notifier.Toast(error, true);
                }
            }
            catch (Exception ex)
            {
                SetStatus(ex.GetType().Name + ": " + ex.Message, StatusKind.Error);
                _notifier.Toast(ex.Message, true);
            }
            finally
            {
                _notifier.Invalidate();
            }
        }

        private void SetStatus(string text, StatusKind kind)
        {
            StatusText.Value = text;
            Status.Value = kind;
        }
    }
}
