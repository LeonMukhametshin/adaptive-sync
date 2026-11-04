using System;

namespace Sync.Core.Schema
{
    public sealed class FieldDefinition
    {
        public FieldDefinition(
            string table,
            string name,
            int introducedIn,
            FieldCategory category,
            FieldType type,
            object? defaultValue = null,
            bool isRequired = false,
            string? references = null)
        {
            Table = table ?? throw new ArgumentNullException(nameof(table));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            IntroducedIn = introducedIn;
            Category = category;
            Type = type;
            DefaultValue = defaultValue;
            IsRequired = isRequired;
            References = references;
        }

        public string Table { get; }

        public string Name { get; }

        public int IntroducedIn { get; }

        public FieldCategory Category { get; }

        public FieldType Type { get; }

        public object? DefaultValue { get; }

        public bool IsRequired { get; }

        public string? References { get; }

        public bool IsNullable => !IsRequired && DefaultValue == null;

        public FieldRef Ref => new FieldRef(Table, Name);

        public override string ToString() => Ref + " (v" + IntroducedIn + ", " + Category.ToStorageName() + ")";
    }
}
