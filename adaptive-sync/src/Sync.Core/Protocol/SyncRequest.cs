using System;
using System.Collections.Generic;

namespace Sync.Core.Protocol
{
    public enum OperationType
    {
        Insert,
        Update,
        Delete,
    }

    public sealed class FieldChange
    {
        public string Field { get; set; } = string.Empty;

        public object? OldValue { get; set; }

        public object? NewValue { get; set; }
    }

    public sealed class SyncOperation
    {
        public Guid OpId { get; set; }

        public string Table { get; set; } = string.Empty;

        public Guid RecordId { get; set; }

        public OperationType Type { get; set; }

        public long BaseRowVersion { get; set; }

        public List<FieldChange> Changes { get; set; } = new List<FieldChange>();

        public long LogicalTime { get; set; }
    }

    public sealed class SyncRequest
    {
        public string ClientId { get; set; } = string.Empty;

        public string AppVersion { get; set; } = string.Empty;

        public int SchemaVersion { get; set; }

        public long SinceServerVersion { get; set; }

        public List<SyncOperation> Operations { get; set; } = new List<SyncOperation>();
    }
}
