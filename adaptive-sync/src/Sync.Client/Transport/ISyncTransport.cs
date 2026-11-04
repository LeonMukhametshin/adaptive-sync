using System.Threading;
using System.Threading.Tasks;
using Sync.Core.Protocol;

namespace Sync.Client.Transport
{
    public interface ISyncTransport
    {
        Task<SyncResponse> SendAsync(SyncRequest request, CancellationToken cancellationToken = default);
    }
}
