using Sync.Core.Protocol;

namespace Sync.Core.Strategies
{
    public interface ISyncStrategy
    {
        string Name { get; }

        StrategyDecision Decide(SyncContext context, SyncOperation operation, ServerRecord? current);
    }
}
