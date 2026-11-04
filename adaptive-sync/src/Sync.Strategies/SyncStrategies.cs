using System;
using System.Collections.Generic;
using System.Linq;
using Sync.Core.Strategies;

namespace Sync.Strategies
{
    public static class SyncStrategies
    {
        private static readonly Dictionary<string, Func<ISyncStrategy>> Factories =
            new Dictionary<string, Func<ISyncStrategy>>(StringComparer.OrdinalIgnoreCase)
            {
                [ServerWinsStrategy.StrategyName] = () => new ServerWinsStrategy(),
                [LwwStrategy.StrategyName] = () => new LwwStrategy(),
                [ClientWinsStrategy.StrategyName] = () => new ClientWinsStrategy(),
                [FieldLwwNoSchemaStrategy.StrategyName] = () => new FieldLwwNoSchemaStrategy(),
                [AdaptiveStrategy.StrategyName] = () => new AdaptiveStrategy(),
            };

        public static IReadOnlyCollection<string> Names => Factories.Keys.ToList();

        public static ISyncStrategy Create(string name) =>
            Factories.TryGetValue(name, out var factory)
                ? factory()
                : throw new ArgumentException(
                    "Unknown sync strategy '" + name + "'. Known: " + string.Join(", ", Factories.Keys) + ".", nameof(name));
    }
}
