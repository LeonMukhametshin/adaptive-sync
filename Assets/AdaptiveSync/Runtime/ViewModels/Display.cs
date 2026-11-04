#nullable enable
using System;
using System.Globalization;
using Sync.Core.Strategies;

namespace AdaptiveSync.Demo.ViewModels
{
    public static class Display
    {
        public static string Short(Guid id) => id.ToString("D").Substring(0, 8);

        public static string Value(object? value) =>
            value switch
            {
                null => "—",
                bool b => b ? "да" : "нет",
                Guid g => Short(g),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            };

        public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        public static string Situation(SyncSituation situation) =>
            situation switch
            {
                SyncSituation.S1 => "S1 · версии схемы совпадают, конфликта нет",
                SyncSituation.S2 => "S2 · старый клиент, поля новых версий не тронуты",
                SyncSituation.S3 => "S3 · сервер менял неизвестные клиенту поля — они сохранены",
                SyncSituation.S4 => "S4 · конфликт решён по категории поля",
                SyncSituation.S5 => "S5 · удаление старым клиентом: tombstone, данные сохранены",
                _ => situation.ToString(),
            };

        public static string FieldOutcomeText(FieldOutcome outcome) =>
            outcome switch
            {
                FieldOutcome.Applied => "применено",
                FieldOutcome.Merged => "слито с серверным значением",
                FieldOutcome.Rejected => "оставлено серверное значение",
                FieldOutcome.Overwritten => "затёрто значением по умолчанию",
                _ => outcome.ToString(),
            };

        public static string StrategyLabel(string strategy) =>
            strategy switch
            {
                "Adaptive" => "Адаптивный",
                "ServerWins" => "Server Wins",
                "Lww" => "LWW",
                "ClientWins" => "Client Wins",
                "FieldLwwNoSchema" => "LWW по полям",
                _ => strategy,
            };
    }

    public sealed class ToastMessage
    {
        public ToastMessage(string text, bool isError)
        {
            Text = text;
            IsError = isError;
        }

        public string Text { get; }

        public bool IsError { get; }
    }

    public interface IAppNotifier
    {
        bool IsBusy { get; }

        void Invalidate();

        void Toast(string text, bool isError);

        void ToggleOnline();
    }
}
