#nullable enable
using AdaptiveSync.Demo.Services;
using Sync.Core.Schema;
using UnityEngine;

namespace AdaptiveSync.Demo.App
{
    public sealed class AppSettings
    {
        public const string EditorServerUrl = "http://localhost:5080/";
        public const string DeviceServerUrl = "http://192.168.0.218:5080/";
        public static readonly int[] ClientVersions = { SchemaVersions.V1, SchemaVersions.V3 };

        private const string ServerUrlKey = "adaptive-sync.server-url";
        private const string SchemaVersionKey = "adaptive-sync.schema-version";

        private readonly bool _persistent;

        private AppSettings(string serverUrl, int schemaVersion, bool persistent)
        {
            ServerUrl = serverUrl;
            SchemaVersion = schemaVersion;
            _persistent = persistent;
        }

        public static AppSettings InMemory(string serverUrl, int schemaVersion) =>
            new AppSettings(SyncDemoSession.NormalizeUrl(serverUrl), schemaVersion, persistent: false);

        public string ServerUrl { get; set; }

        public int SchemaVersion { get; set; }

        public static string DefaultServerUrl => Application.isEditor ? EditorServerUrl : DeviceServerUrl;

        public static AppSettings Load()
        {
            var version = PlayerPrefs.GetInt(SchemaVersionKey, SchemaVersions.V1);
            if (System.Array.IndexOf(ClientVersions, version) < 0) version = SchemaVersions.V1;
            return new AppSettings(
                SyncDemoSession.NormalizeUrl(PlayerPrefs.GetString(ServerUrlKey, DefaultServerUrl)),
                version,
                persistent: true);
        }

        public void Save()
        {
            if (!_persistent) return;
            PlayerPrefs.SetString(ServerUrlKey, ServerUrl);
            PlayerPrefs.SetInt(SchemaVersionKey, SchemaVersion);
            PlayerPrefs.Save();
        }
    }
}
