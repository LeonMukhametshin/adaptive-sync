using System;
using System.Threading;
using System.Threading.Tasks;
using AdaptiveSync.Demo.App;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AdaptiveSync.Demo.Boot.Steps
{
    public sealed class LoadSceneStep : IBootStep
    {
        private readonly string _sceneName;
        private readonly float _minimumSeconds;

        public LoadSceneStep(string sceneName, float minimumSeconds = 0f)
        {
            _sceneName = sceneName;
            _minimumSeconds = Math.Max(0f, minimumSeconds);
        }

        public string Title => "Открытие главного экрана";

        public float Weight => 3f;

        public async Task RunAsync(AppServices services, IProgress<float> progress, CancellationToken cancellationToken)
        {
            var start = Time.realtimeSinceStartup;
            var operation = SceneManager.LoadSceneAsync(_sceneName, LoadSceneMode.Single);
            if (operation == null)
                throw new InvalidOperationException("Scene '" + _sceneName + "' is not in the build settings.");
            operation.allowSceneActivation = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var loaded = Mathf.Clamp01(operation.progress / 0.9f);
                var byTime = _minimumSeconds <= 0f ? 1f : Mathf.Clamp01((Time.realtimeSinceStartup - start) / _minimumSeconds);
                progress.Report(Mathf.Min(loaded, byTime));
                if (loaded >= 1f && byTime >= 1f) break;
                await Task.Yield();
            }

            operation.allowSceneActivation = true;
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }
    }
}
