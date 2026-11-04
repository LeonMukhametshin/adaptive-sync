using System;
using System.Collections.Generic;
using System.Linq;
using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Strategies;

namespace Sync.Strategies
{
    public sealed class AdaptiveStrategy : ISyncStrategy
    {
        public const string StrategyName = "Adaptive";

        private readonly bool _useSchemaVersion;
        private readonly bool _useCategories;
        private readonly bool _fieldLevelConflicts;

        public AdaptiveStrategy()
            : this(StrategyName, useSchemaVersion: true, useCategories: true, fieldLevelConflicts: true)
        {
        }

        private AdaptiveStrategy(string name, bool useSchemaVersion, bool useCategories, bool fieldLevelConflicts)
        {
            Name = name;
            _useSchemaVersion = useSchemaVersion;
            _useCategories = useCategories;
            _fieldLevelConflicts = fieldLevelConflicts;
        }

        public string Name { get; }

        public static AdaptiveStrategy WithoutSchemaVersion() =>
            new AdaptiveStrategy(StrategyName + "-NoSchema", useSchemaVersion: false, useCategories: true, fieldLevelConflicts: true);

        public static AdaptiveStrategy WithoutCategories() =>
            new AdaptiveStrategy(StrategyName + "-NoCategories", useSchemaVersion: true, useCategories: false, fieldLevelConflicts: true);

        public static AdaptiveStrategy WithRecordLevelConflicts() =>
            new AdaptiveStrategy(StrategyName + "-RecordConflict", useSchemaVersion: true, useCategories: true, fieldLevelConflicts: false);

        public StrategyDecision Decide(SyncContext context, SyncOperation operation, ServerRecord? current)
        {
            var table = context.Registry.GetTable(operation.Table);
            var isOlder = _useSchemaVersion && context.ClientSchemaVersion < context.Registry.LatestVersion;

            if (operation.Type == OperationType.Insert && current == null)
                return ApplyInsert(operation, isOlder ? SyncSituation.S2 : SyncSituation.S1);

            if (StrategyRules.IsMissing(current))
                return StrategyRules.RejectAll(operation, StrategyRules.MissingRecord);

            return operation.Type == OperationType.Delete
                ? DecideDelete(context, table, current!, isOlder)
                : DecideChanges(context, table, operation, current!, isOlder);
        }

        private static StrategyDecision ApplyInsert(SyncOperation operation, SyncSituation situation)
        {
            var decision = new StrategyDecision { Outcome = OperationOutcome.Applied, Situation = situation };
            foreach (var change in operation.Changes)
            {
                decision.FieldWrites[change.Field] = change.NewValue;
                decision.Fields.Add(new FieldDecision(change.Field, FieldOutcome.Applied, situation));
            }
            return decision;
        }

        private StrategyDecision DecideChanges(
            SyncContext context, TableDefinition table, SyncOperation operation, ServerRecord current, bool isOlder)
        {
            var unknownChanged = isOlder && table.Fields.Any(f =>
                !context.IsKnown(table.Name, f.Name) && ChangedAfterBase(current, f.Name, operation.BaseRowVersion));
            var noConflict = !isOlder ? SyncSituation.S1 : unknownChanged ? SyncSituation.S3 : SyncSituation.S2;

            var decision = new StrategyDecision();
            var notes = new List<string>();
            foreach (var change in operation.Changes)
            {
                if (!context.IsKnown(table.Name, change.Field))
                {
                    decision.Fields.Add(new FieldDecision(change.Field, FieldOutcome.Rejected));
                    notes.Add(change.Field + ": outside K(v" + context.ClientSchemaVersion + ")");
                    continue;
                }

                if (!InConflict(current, change.Field, operation.BaseRowVersion))
                {
                    decision.FieldWrites[change.Field] = change.NewValue;
                    decision.Fields.Add(new FieldDecision(change.Field, FieldOutcome.Applied, noConflict));
                    continue;
                }

                var field = table.FindField(change.Field)!;
                var resolved = ResolveConflict(field, operation, current, change);
                if (resolved.Outcome != FieldOutcome.Rejected)
                    decision.FieldWrites[change.Field] = resolved.Value;
                decision.Fields.Add(new FieldDecision(change.Field, resolved.Outcome, SyncSituation.S4));
                notes.Add(change.Field + ": " + resolved.Note);
            }

            decision.Situation = decision.Fields.Any(f => f.Situation == SyncSituation.S4) ? SyncSituation.S4 : noConflict;
            decision.Outcome = decision.FieldWrites.Count == 0 && operation.Changes.Count > 0 ? OperationOutcome.Rejected
                : notes.Count > 0 ? OperationOutcome.Merged
                : OperationOutcome.Applied;
            if (notes.Count > 0)
                decision.Message = "Resolved by field rules: " + string.Join("; ", notes) + ".";
            return decision;
        }

        private bool InConflict(ServerRecord current, string field, long baseRowVersion) =>
            _fieldLevelConflicts ? ChangedAfterBase(current, field, baseRowVersion) : current.RowVersion > baseRowVersion;

        private Resolution ResolveConflict(FieldDefinition field, SyncOperation operation, ServerRecord current, FieldChange change)
        {
            var stored = current.Values.TryGetValue(field.Name, out var value) ? value : field.DefaultValue;
            switch (_useCategories ? field.Category : FieldCategory.Scalar)
            {
                case FieldCategory.Counter:
                    var delta = AsLong(change.NewValue) - AsLong(change.OldValue);
                    return new Resolution(FieldOutcome.Merged, AsLong(stored) + delta, "counter merged with delta " + delta);
                case FieldCategory.UserOwned:
                case FieldCategory.UserPreference:
                    return new Resolution(FieldOutcome.Applied, change.NewValue, "client owns the value");
                case FieldCategory.ServerAuthoritative:
                    return new Resolution(FieldOutcome.Rejected, stored, "server is authoritative");
                case FieldCategory.Scalar:
                    return operation.LogicalTime > StrategyRules.FieldTime(current, field.Name)
                        ? new Resolution(FieldOutcome.Applied, change.NewValue, "client change is newer")
                        : new Resolution(FieldOutcome.Rejected, stored, "server change is newer");
                default:
                    throw new ArgumentOutOfRangeException(nameof(field), field.Category, null);
            }
        }

        private static StrategyDecision DecideDelete(SyncContext context, TableDefinition table, ServerRecord current, bool isOlder)
        {
            if (!isOlder)
                return new StrategyDecision { Outcome = OperationOutcome.Applied, HardDelete = true, Situation = SyncSituation.S1 };

            var unknownData = table.Fields
                .Where(f => !context.IsKnown(table.Name, f.Name) && HasData(f, current))
                .Select(f => f.Name)
                .ToList();
            var unknownDependents = current.DependentTables
                .Where(t => context.Registry.GetTable(t).IntroducedIn > context.ClientSchemaVersion)
                .ToList();

            if (unknownData.Count == 0 && unknownDependents.Count == 0)
                return new StrategyDecision { Outcome = OperationOutcome.Applied, HardDelete = true, Situation = SyncSituation.S2 };

            return new StrategyDecision
            {
                Outcome = OperationOutcome.Tombstoned,
                Tombstone = true,
                Situation = SyncSituation.S5,
                Message = "Tombstone keeps data unknown to v" + context.ClientSchemaVersion + ": " +
                          string.Join(", ", unknownData.Concat(unknownDependents)) + ".",
            };
        }

        private static bool ChangedAfterBase(ServerRecord current, string field, long baseRowVersion) =>
            current.FieldVersions.TryGetValue(field, out var version) && version.RowVersion > baseRowVersion;

        private static bool HasData(FieldDefinition field, ServerRecord current) =>
            current.Values.TryGetValue(field.Name, out var value) && !FieldValues.AreEqual(field.Type, value, field.DefaultValue);

        private static long AsLong(object? value) => value == null ? 0L : Convert.ToInt64(value);

        private readonly struct Resolution
        {
            public Resolution(FieldOutcome outcome, object? value, string note)
            {
                Outcome = outcome;
                Value = value;
                Note = note;
            }

            public FieldOutcome Outcome { get; }

            public object? Value { get; }

            public string Note { get; }
        }
    }
}
