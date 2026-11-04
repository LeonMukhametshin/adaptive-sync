using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sync.Core.Resolver;

namespace Sync.Core.Protocol
{
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(SyncRequest))]
    [JsonSerializable(typeof(SyncResponse))]
    [JsonSerializable(typeof(ResolveRequest))]
    [JsonSerializable(typeof(ResolveResponse))]
    [JsonSerializable(typeof(ResolverHealth))]
    [JsonSerializable(typeof(StrategySwitchRequest))]
    [JsonSerializable(typeof(List<FieldChange>))]
    [JsonSerializable(typeof(Dictionary<string, object>))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(long))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(Guid))]
    public partial class SyncJsonContext : JsonSerializerContext
    {
    }
}
