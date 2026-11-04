using System;
using System.Threading;
using System.Threading.Tasks;
using AdaptiveSync.Demo.App;
using AdaptiveSync.Demo.Services;

namespace AdaptiveSync.Demo.Boot.Steps
{
    public sealed class CheckServerStep : IBootStep
    {
        public string Title => "Проверка сервера синхронизации";

        public float Weight => 2f;

        public async Task RunAsync(AppServices services, IProgress<float> progress, CancellationToken cancellationToken)
        {
            var status = new ServerStatus();
            services.Register(status);
            await status.CheckAsync(services.Get<AppSettings>().ServerUrl);
        }
    }
}
