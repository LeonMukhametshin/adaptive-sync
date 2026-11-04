using Sync.Core.Protocol;
using Sync.Core.Strategies;

namespace Sync.Strategies
{
    public sealed class LwwStrategy : ISyncStrategy
    {
        public const string StrategyName = "Lww";

        public string Name => StrategyName;

        public StrategyDecision Decide(SyncContext context, SyncOperation operation, ServerRecord? current)
        {
            if (operation.Type == OperationType.Insert && current == null)
                return StrategyRules.ApplyChanges(operation);

            if (StrategyRules.IsMissing(current))
                return StrategyRules.RejectAll(operation, StrategyRules.MissingRecord);

            var serverTime = StrategyRules.RecordTime(current!);
            if (operation.LogicalTime <= serverTime)
                return StrategyRules.RejectAll(operation, StrategyRules.OlderThanServer(operation.LogicalTime, serverTime));

            return operation.Type == OperationType.Delete
                ? StrategyRules.Delete()
                : StrategyRules.OverwriteWholeRecord(context, operation);
        }
    }
}
