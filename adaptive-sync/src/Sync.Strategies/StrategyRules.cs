using System.Linq;
using Sync.Core.Protocol;
using Sync.Core.Strategies;

namespace Sync.Strategies
{
    internal static class StrategyRules
    {
        public const string MissingRecord = "Record does not exist on the server.";

        public static long RecordTime(ServerRecord record) =>
            record.FieldVersions.Count == 0 ? 0 : record.FieldVersions.Values.Max(v => v.LogicalTime);

        public static long FieldTime(ServerRecord record, string field) =>
            record.FieldVersions.TryGetValue(field, out var version) ? version.LogicalTime : 0;

        public static bool IsMissing(ServerRecord? current) => current == null || current.IsDeleted;

        public static StrategyDecision ApplyChanges(SyncOperation operation) =>
            new StrategyDecision
            {
                Outcome = OperationOutcome.Applied,
                FieldWrites = operation.Changes.ToDictionary(c => c.Field, c => c.NewValue),
                Fields = operation.Changes.Select(c => new FieldDecision(c.Field, FieldOutcome.Applied)).ToList(),
            };

        public static StrategyDecision OverwriteWholeRecord(SyncContext context, SyncOperation operation)
        {
            var decision = ApplyChanges(operation);
            var table = context.Registry.GetTable(operation.Table);
            foreach (var field in table.Fields.Where(f => !context.IsKnown(table.Name, f.Name)))
            {
                decision.FieldWrites[field.Name] = field.DefaultValue;
                decision.Fields.Add(new FieldDecision(field.Name, FieldOutcome.Overwritten));
            }
            return decision;
        }

        public static StrategyDecision Delete() =>
            new StrategyDecision { Outcome = OperationOutcome.Applied, HardDelete = true };

        public static StrategyDecision RejectAll(SyncOperation operation, string message) =>
            new StrategyDecision
            {
                Outcome = OperationOutcome.Rejected,
                Message = message,
                Fields = operation.Changes.Select(c => new FieldDecision(c.Field, FieldOutcome.Rejected)).ToList(),
            };

        public static string OlderThanServer(long operationTime, long serverTime) =>
            "Conflict: operation time " + operationTime + " is not newer than server time " + serverTime + ".";
    }
}
