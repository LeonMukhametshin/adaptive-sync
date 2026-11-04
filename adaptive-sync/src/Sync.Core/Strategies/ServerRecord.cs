using System;
using System.Collections.Generic;

namespace Sync.Core.Strategies
{
    public sealed class FieldVersion
    {
        public FieldVersion(long rowVersion, long logicalTime)
        {
            RowVersion = rowVersion;
            LogicalTime = logicalTime;
        }

        public long RowVersion { get; }

        public long LogicalTime { get; }
    }

    public sealed class ServerRecord
    {
        public string Table { get; set; } = string.Empty;

        public Guid RecordId { get; set; }

        public long RowVersion { get; set; }

        public bool IsDeleted { get; set; }

        public Dictionary<string, object?> Values { get; set; } = new Dictionary<string, object?>();

        public Dictionary<string, FieldVersion> FieldVersions { get; set; } = new Dictionary<string, FieldVersion>();

        public bool HasDependents { get; set; }

        public List<string> DependentTables { get; set; } = new List<string>();
    }
}
