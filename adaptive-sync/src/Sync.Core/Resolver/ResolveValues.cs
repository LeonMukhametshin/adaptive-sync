using System.Collections.Generic;
using System.Linq;
using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Strategies;

namespace Sync.Core.Resolver
{
    public static class ResolveValues
    {
        public static void Normalize(SchemaRegistry registry, ResolveItem item)
        {
            var table = registry.GetTable(item.Operation.Table);
            foreach (var change in item.Operation.Changes)
            {
                var type = Type(table, change.Field);
                change.OldValue = FieldValues.Normalize(type, change.OldValue);
                change.NewValue = FieldValues.Normalize(type, change.NewValue);
            }

            if (item.Current != null)
                item.Current.Values = Normalize(table, item.Current.Values);
        }

        public static void Normalize(SchemaRegistry registry, string table, StrategyDecision decision) =>
            decision.FieldWrites = Normalize(registry.GetTable(table), decision.FieldWrites);

        private static Dictionary<string, object?> Normalize(TableDefinition table, Dictionary<string, object?> values) =>
            values.ToDictionary(p => p.Key, p => FieldValues.Normalize(Type(table, p.Key), p.Value));

        private static FieldType Type(TableDefinition table, string field) =>
            (table.FindField(field) ?? throw new System.FormatException("Unknown field '" + table.Name + "." + field + "'.")).Type;
    }
}
