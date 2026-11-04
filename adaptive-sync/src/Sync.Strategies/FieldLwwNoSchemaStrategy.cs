using System.Collections.Generic;
using System.Linq;
using Sync.Core.Protocol;
using Sync.Core.Strategies;

namespace Sync.Strategies
{
    public sealed class FieldLwwNoSchemaStrategy : ISyncStrategy
    {
        public const string StrategyName = "FieldLwwNoSchema";

        public string Name => StrategyName;

        public StrategyDecision Decide(SyncContext context, SyncOperation operation, ServerRecord? current)
        {
            if (operation.Type == OperationType.Insert && current == null)
                return StrategyRules.ApplyChanges(operation);

            if (StrategyRules.IsMissing(current))
                return StrategyRules.RejectAll(operation, StrategyRules.MissingRecord);

            if (operation.Type == OperationType.Delete)
            {
                var serverTime = StrategyRules.RecordTime(current!);
                return operation.LogicalTime > serverTime
                    ? StrategyRules.Delete()
                    : StrategyRules.RejectAll(operation, StrategyRules.OlderThanServer(operation.LogicalTime, serverTime));
            }

            var decision = new StrategyDecision();
            var rejected = new List<string>();
            foreach (var change in operation.Changes)
            {
                if (operation.LogicalTime > StrategyRules.FieldTime(current!, change.Field))
                {
                    decision.FieldWrites[change.Field] = change.NewValue;
                    decision.Fields.Add(new FieldDecision(change.Field, FieldOutcome.Applied));
                }
                else
                {
                    rejected.Add(change.Field);
                    decision.Fields.Add(new FieldDecision(change.Field, FieldOutcome.Rejected));
                }
            }

            decision.Outcome = rejected.Count == 0 ? OperationOutcome.Applied
                : decision.FieldWrites.Count == 0 ? OperationOutcome.Rejected
                : OperationOutcome.Merged;
            if (rejected.Count > 0)
                decision.Message = "Server has newer values for: " + string.Join(", ", rejected.OrderBy(f => f)) + ".";
            return decision;
        }
    }
}
