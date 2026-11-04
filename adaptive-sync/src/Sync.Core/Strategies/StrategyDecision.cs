using System.Collections.Generic;
using Sync.Core.Protocol;

namespace Sync.Core.Strategies
{
    public enum SyncSituation
    {
        S1,
        S2,
        S3,
        S4,
        S5,
    }

    public enum FieldOutcome
    {
        Applied,
        Merged,
        Rejected,
        Overwritten,
    }

    public sealed class FieldDecision
    {
        public FieldDecision(string field, FieldOutcome outcome, SyncSituation? situation = null)
        {
            Field = field;
            Outcome = outcome;
            Situation = situation;
        }

        public string Field { get; }

        public FieldOutcome Outcome { get; }

        public SyncSituation? Situation { get; }
    }

    public sealed class StrategyDecision
    {
        public OperationOutcome Outcome { get; set; }

        public Dictionary<string, object?> FieldWrites { get; set; } = new Dictionary<string, object?>();

        public bool HardDelete { get; set; }

        public bool Tombstone { get; set; }

        public SyncSituation? Situation { get; set; }

        public List<FieldDecision> Fields { get; set; } = new List<FieldDecision>();

        public string? Message { get; set; }

        public static StrategyDecision Reject(string message) =>
            new StrategyDecision { Outcome = OperationOutcome.Rejected, Message = message };
    }
}
