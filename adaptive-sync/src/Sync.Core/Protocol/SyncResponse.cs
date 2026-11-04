using System;
using System.Collections.Generic;
using Sync.Core.Strategies;

namespace Sync.Core.Protocol
{
    public enum OperationOutcome
    {
        Applied,
        Merged,
        Rejected,
        Tombstoned,
    }

    public sealed class OperationResult
    {
        public Guid OpId { get; set; }

        public OperationOutcome Outcome { get; set; }

        public long RowVersion { get; set; }

        public string? Message { get; set; }

        public SyncSituation? Situation { get; set; }

        public List<FieldResult> Fields { get; set; } = new List<FieldResult>();
    }

    public sealed class FieldResult
    {
        public string Field { get; set; } = string.Empty;

        public FieldOutcome Outcome { get; set; }

        public SyncSituation? Situation { get; set; }
    }

    public sealed class RecordState
    {
        public string Table { get; set; } = string.Empty;

        public Guid RecordId { get; set; }

        public long RowVersion { get; set; }

        public bool IsDeleted { get; set; }

        public Dictionary<string, object?> Values { get; set; } = new Dictionary<string, object?>();
    }

    public sealed class SyncResponse
    {
        public List<OperationResult> Results { get; set; } = new List<OperationResult>();

        public List<RecordState> Records { get; set; } = new List<RecordState>();

        public List<RecordState> RemoteChanges { get; set; } = new List<RecordState>();

        public long ServerVersion { get; set; }
    }
}
