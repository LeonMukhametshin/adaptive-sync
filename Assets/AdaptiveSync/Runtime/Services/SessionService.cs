#nullable enable
using System;
using System.IO;
using AdaptiveSync.Demo.App;
using Sync.Core.Time;

namespace AdaptiveSync.Demo.Services
{
    public sealed class SessionService : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly string _databaseDirectory;
        private SyncDemoSession? _current;

        public SessionService(AppSettings settings, string databaseDirectory)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _databaseDirectory = databaseDirectory ?? throw new ArgumentNullException(nameof(databaseDirectory));
        }

        public event Action? Changed;

        public SyncDemoSession Current =>
            _current ?? throw new InvalidOperationException("Local storage is not open yet.");

        public bool IsOpen => _current != null;

        public string ServerUrl => _settings.ServerUrl;

        public int SchemaVersion => _settings.SchemaVersion;

        public static string ClientIdFor(int schemaVersion) => "unity-v" + schemaVersion;

        public string DatabasePathFor(int schemaVersion) =>
            Path.Combine(_databaseDirectory, "adaptive-sync-" + ClientIdFor(schemaVersion) + ".db");

        public void Open(int schemaVersion)
        {
            if (Array.IndexOf(AppSettings.ClientVersions, schemaVersion) < 0)
                throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "Unsupported client version.");

            var online = _current?.IsOnline ?? true;
            CloseCurrent();
            _current = new SyncDemoSession(
                _settings.ServerUrl, DatabasePathFor(schemaVersion), ClientIdFor(schemaVersion), schemaVersion,
                new LogicalClock(), Guid.NewGuid) { IsOnline = online };

            _settings.SchemaVersion = schemaVersion;
            _settings.Save();
            Changed?.Invoke();
        }

        public void ChangeServerUrl(string serverUrl)
        {
            var url = SyncDemoSession.NormalizeUrl(serverUrl);
            if (url == _settings.ServerUrl && _current != null) return;
            _settings.ServerUrl = url;
            _settings.Save();
            Open(_settings.SchemaVersion);
        }

        public void ResetDatabase()
        {
            var version = _settings.SchemaVersion;
            CloseCurrent();
            var path = DatabasePathFor(version);
            if (File.Exists(path)) File.Delete(path);
            Open(version);
        }

        public void Dispose() => CloseCurrent();

        private void CloseCurrent()
        {
            _current?.Dispose();
            _current = null;
        }
    }
}
