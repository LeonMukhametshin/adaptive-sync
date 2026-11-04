using System.Collections.Generic;
using Sync.Core.Protocol;
using Sync.Core.Strategies;

namespace Sync.Core.Resolver
{
    public static class ResolveContract
    {
        public const int Version = 1;
        public const string ResolvePath = "resolve";
        public const string HealthPath = "health";
        public const string StrategyPath = "strategy";
    }

    public sealed class ResolveItem
    {
        public SyncOperation Operation { get; set; } = new SyncOperation();

        public ServerRecord? Current { get; set; }
    }

    public sealed class ResolveRequest
    {
        public int ContractVersion { get; set; } = ResolveContract.Version;

        public string ClientId { get; set; } = string.Empty;

        public int SchemaVersion { get; set; }

        public List<ResolveItem> Items { get; set; } = new List<ResolveItem>();
    }

    public sealed class ResolveResponse
    {
        public string Strategy { get; set; } = string.Empty;

        public List<StrategyDecision> Decisions { get; set; } = new List<StrategyDecision>();
    }

    public sealed class ResolverHealth
    {
        public string Status { get; set; } = string.Empty;

        public string Strategy { get; set; } = string.Empty;

        public int ContractVersion { get; set; }

        public bool AllowStrategySwitch { get; set; }
    }

    public sealed class StrategySwitchRequest
    {
        public string Strategy { get; set; } = string.Empty;
    }
}
