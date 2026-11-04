using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sync.Core.Protocol
{
    public static class SyncJson
    {
        public static JsonSerializerOptions Options => SyncJsonContext.Default.Options;

        public static JsonSerializerOptions Configure(JsonSerializerOptions options)
        {
            options.TypeInfoResolverChain.Insert(0, SyncJsonContext.Default);
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
