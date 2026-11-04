using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sync.Core.Protocol;

namespace Sync.Client.Transport
{
    public sealed class HttpSyncTransport : ISyncTransport
    {
        private readonly HttpClient _http;
        private readonly string _path;

        public HttpSyncTransport(HttpClient http, string path = "sync")
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _path = path;
        }

        public async Task<SyncResponse> SendAsync(SyncRequest request, CancellationToken cancellationToken = default)
        {
            var body = JsonSerializer.Serialize(request, SyncJson.Options);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_path, content, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    "Sync request failed with " + (int)response.StatusCode + " " + response.ReasonPhrase + ": " + text);
            }

            return JsonSerializer.Deserialize<SyncResponse>(text, SyncJson.Options)
                ?? throw new InvalidOperationException("Sync response is empty.");
        }
    }
}
