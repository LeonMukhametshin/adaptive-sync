using System;
using System.Threading;
using System.Threading.Tasks;
using AdaptiveSync.Demo.App;

namespace AdaptiveSync.Demo.Boot
{
    public interface IBootStep
    {
        string Title { get; }

        float Weight { get; }

        Task RunAsync(AppServices services, IProgress<float> progress, CancellationToken cancellationToken);
    }
}
