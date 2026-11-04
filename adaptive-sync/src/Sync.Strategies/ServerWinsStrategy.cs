using System.Globalization;
using Sync.Core.Protocol;
using Sync.Core.Strategies;

namespace Sync.Strategies
{
    public sealed class ServerWinsStrategy : ISyncStrategy
    {
        public const string StrategyName = "ServerWins";

        public string Name => StrategyName;

        public StrategyDecision Decide(SyncContext context, SyncOperation operation, ServerRecord? current)
        {
            if (operation.Type == OperationType.Insert)
            {
                return current == null
                    ? StrategyRules.ApplyChanges(operation)
                    : StrategyRules.RejectAll(operation, "Record already exists on the server.");
            }

            if (StrategyRules.IsMissing(current))
                return StrategyRules.RejectAll(operation, StrategyRules.MissingRecord);

            if (current!.RowVersion > operation.BaseRowVersion)
            {
                return StrategyRules.RejectAll(operation, string.Format(CultureInfo.InvariantCulture,
                    "Conflict: server row_version {0} is newer than base {1}.", current.RowVersion, operation.BaseRowVersion));
            }

            return operation.Type == OperationType.Delete ? StrategyRules.Delete() : StrategyRules.ApplyChanges(operation);
        }
    }
}
