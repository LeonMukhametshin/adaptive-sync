using System.Collections.Generic;
using System.Linq;

namespace Sync.Core.Strategies
{
    public sealed class OwnWrites
    {
        private readonly HashSet<long> _versions = new HashSet<long>();
        private readonly HashSet<string> _contested = new HashSet<string>();

        public void Add(long rowVersion) => _versions.Add(rowVersion);

        public ServerRecord Mask(ServerRecord current, long baseRowVersion)
        {
            foreach (var pair in current.FieldVersions)
            {
                if (pair.Value.RowVersion > baseRowVersion && !_versions.Contains(pair.Value.RowVersion))
                    _contested.Add(pair.Key);
            }

            if (_versions.Count == 0) return current;

            var fieldVersions = current.FieldVersions.ToDictionary(
                p => p.Key,
                p => _versions.Contains(p.Value.RowVersion) && !_contested.Contains(p.Key)
                    ? new FieldVersion(baseRowVersion, p.Value.LogicalTime)
                    : p.Value);
            var rowVersion = _versions.Contains(current.RowVersion)
                ? fieldVersions.Values.Select(v => v.RowVersion).Concat(new[] { baseRowVersion }).Max()
                : current.RowVersion;

            return new ServerRecord
            {
                Table = current.Table,
                RecordId = current.RecordId,
                RowVersion = rowVersion,
                IsDeleted = current.IsDeleted,
                Values = current.Values,
                FieldVersions = fieldVersions,
                HasDependents = current.HasDependents,
                DependentTables = current.DependentTables,
            };
        }
    }
}
