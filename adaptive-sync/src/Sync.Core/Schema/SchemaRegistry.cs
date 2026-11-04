using System;
using System.Collections.Generic;
using System.Linq;

namespace Sync.Core.Schema
{
    public sealed class SchemaRegistry
    {
        public const string IdColumn = "id";
        public const string RowVersionColumn = "row_version";
        public const string IsDeletedColumn = "is_deleted";

        public const string FieldVersionsColumn = "field_versions";
        public const string DeletedAtColumn = "deleted_at";

        private readonly Dictionary<string, TableDefinition> _tablesByName;
        private readonly Dictionary<int, HashSet<FieldRef>> _knownFields;

        public SchemaRegistry(int latestVersion, IEnumerable<TableDefinition> tables)
        {
            if (latestVersion < 1)
                throw new ArgumentOutOfRangeException(nameof(latestVersion), latestVersion, "Schema versions start at 1.");

            LatestVersion = latestVersion;
            Tables = tables.ToList();
            Validate(LatestVersion, Tables);

            _tablesByName = Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
            _knownFields = new Dictionary<int, HashSet<FieldRef>>();
            for (var v = MinVersion; v <= LatestVersion; v++)
            {
                var version = v;
                _knownFields[v] = new HashSet<FieldRef>(Tables.SelectMany(t => t.FieldsFor(version)).Select(f => f.Ref));
            }
        }

        public const int MinVersion = 1;

        public int LatestVersion { get; }

        public IReadOnlyList<TableDefinition> Tables { get; }

        public IEnumerable<FieldDefinition> AllFields => Tables.SelectMany(t => t.Fields);

        public TableDefinition GetTable(string name) =>
            _tablesByName.TryGetValue(name, out var table)
                ? table
                : throw new KeyNotFoundException("Unknown table '" + name + "'.");

        public FieldDefinition GetField(string table, string field) =>
            GetTable(table).FindField(field) ?? throw new KeyNotFoundException("Unknown field '" + table + "." + field + "'.");

        public IEnumerable<TableDefinition> TablesFor(int version)
        {
            EnsureVersion(version);
            return Tables.Where(t => t.IntroducedIn <= version);
        }

        public IReadOnlyCollection<FieldRef> KnownFields(int version)
        {
            EnsureVersion(version);
            return _knownFields[version];
        }

        public bool IsKnown(int version, string table, string field)
        {
            EnsureVersion(version);
            return _knownFields[version].Contains(new FieldRef(table, field));
        }

        public bool IsSupported(int version) => version >= MinVersion && version <= LatestVersion;

        private void EnsureVersion(int version)
        {
            if (!IsSupported(version))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(version), version, "Supported schema versions are " + MinVersion + ".." + LatestVersion + ".");
            }
        }

        private static void Validate(int latestVersion, IReadOnlyList<TableDefinition> tables)
        {
            var seenTables = new HashSet<string>(StringComparer.Ordinal);
            var reserved = new[] { IdColumn, RowVersionColumn, IsDeletedColumn, FieldVersionsColumn, DeletedAtColumn };

            foreach (var table in tables)
            {
                if (!seenTables.Add(table.Name))
                    throw new ArgumentException("Duplicate table '" + table.Name + "'.");
                if (table.IntroducedIn < MinVersion || table.IntroducedIn > latestVersion)
                    throw new ArgumentException("Table '" + table.Name + "' has invalid version " + table.IntroducedIn + ".");

                var seenFields = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in table.Fields)
                {
                    if (!string.Equals(field.Table, table.Name, StringComparison.Ordinal))
                        throw new ArgumentException("Field '" + field.Ref + "' is declared in table '" + table.Name + "'.");
                    if (!seenFields.Add(field.Name))
                        throw new ArgumentException("Duplicate field '" + field.Ref + "'.");
                    if (Array.IndexOf(reserved, field.Name) >= 0)
                        throw new ArgumentException("Field '" + field.Ref + "' uses a reserved service column name.");
                    if (field.IntroducedIn < table.IntroducedIn || field.IntroducedIn > latestVersion)
                        throw new ArgumentException("Field '" + field.Ref + "' has invalid version " + field.IntroducedIn + ".");
                    if (field.IsRequired && field.IntroducedIn > table.IntroducedIn)
                        throw new ArgumentException("Field '" + field.Ref + "' added to an existing table must be optional.");

                    if (field.References != null)
                    {
                        var target = tables.TakeWhile(t => t != table)
                            .FirstOrDefault(t => string.Equals(t.Name, field.References, StringComparison.Ordinal));
                        if (target == null || target.IntroducedIn > field.IntroducedIn)
                            throw new ArgumentException("Field '" + field.Ref + "' references unknown table '" + field.References + "'.");
                        if (field.Type != FieldType.Uuid)
                            throw new ArgumentException("Reference field '" + field.Ref + "' must be a uuid.");
                    }
                }
            }
        }
    }
}
