using System;
using System.Collections.Generic;
using System.Linq;

namespace Sync.Core.Schema
{
    public sealed class TableDefinition
    {
        public TableDefinition(string name, int introducedIn, IEnumerable<FieldDefinition> fields)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            IntroducedIn = introducedIn;
            Fields = fields.ToList();
        }

        public string Name { get; }

        public int IntroducedIn { get; }

        public IReadOnlyList<FieldDefinition> Fields { get; }

        public FieldDefinition? FindField(string name) =>
            Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));

        public IEnumerable<FieldDefinition> FieldsFor(int version) =>
            IntroducedIn <= version ? Fields.Where(f => f.IntroducedIn <= version) : Enumerable.Empty<FieldDefinition>();
    }
}
