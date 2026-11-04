using System;
using System.Threading;
using System.Threading.Tasks;
using AdaptiveSync.Demo.App;

namespace AdaptiveSync.Demo.Boot.Steps
{
    public sealed class LoadSettingsStep : IBootStep
    {
        public string Title => "Загрузка настроек";

        public float Weight => 1f;

        public Task RunAsync(AppServices services, IProgress<float> progress, CancellationToken cancellationToken)
        {
            services.Register(AppSettings.Load());
            return Task.CompletedTask;
        }
    }
}
