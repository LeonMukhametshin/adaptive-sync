using System;
using System.Threading;
using System.Threading.Tasks;
using AdaptiveSync.Demo.App;
using AdaptiveSync.Demo.Services;
using UnityEngine;

namespace AdaptiveSync.Demo.Boot.Steps
{
    public sealed class OpenStorageStep : IBootStep
    {
        private readonly string _databaseDirectory;

        public OpenStorageStep(string databaseDirectory = null)
        {
            _databaseDirectory = databaseDirectory;
        }

        public string Title => "Открытие локальной базы";

        public float Weight => 2f;

        public Task RunAsync(AppServices services, IProgress<float> progress, CancellationToken cancellationToken)
        {
            var settings = services.Get<AppSettings>();
            var sessions = new SessionService(settings, _databaseDirectory ?? Application.persistentDataPath);
            sessions.Open(settings.SchemaVersion);
            services.Register(sessions);
            return Task.CompletedTask;
        }
    }
}
