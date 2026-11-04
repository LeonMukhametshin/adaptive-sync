using System;
using System.Collections.Generic;
using Sync.Core.Schema;

namespace Sync.Core.Strategies
{
    public sealed class SyncContext
    {
        public SyncContext(SchemaRegistry registry, string clientId, int clientSchemaVersion)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            ClientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
            ClientSchemaVersion = clientSchemaVersion;
            KnownFields = registry.KnownFields(clientSchemaVersion);
        }

        public SchemaRegistry Registry { get; }

        public string ClientId { get; }

        public int ClientSchemaVersion { get; }

        public IReadOnlyCollection<FieldRef> KnownFields { get; }

        public bool IsKnown(string table, string field) => Registry.IsKnown(ClientSchemaVersion, table, field);
    }
}
