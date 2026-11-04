using System;

namespace Sync.Core.Schema
{
    public readonly struct FieldRef : IEquatable<FieldRef>
    {
        public FieldRef(string table, string field)
        {
            Table = table ?? throw new ArgumentNullException(nameof(table));
            Field = field ?? throw new ArgumentNullException(nameof(field));
        }

        public string Table { get; }

        public string Field { get; }

        public bool Equals(FieldRef other) =>
            string.Equals(Table, other.Table, StringComparison.Ordinal) &&
            string.Equals(Field, other.Field, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is FieldRef other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Table, Field);

        public override string ToString() => Table + "." + Field;

        public static bool operator ==(FieldRef left, FieldRef right) => left.Equals(right);

        public static bool operator !=(FieldRef left, FieldRef right) => !left.Equals(right);
    }
}
