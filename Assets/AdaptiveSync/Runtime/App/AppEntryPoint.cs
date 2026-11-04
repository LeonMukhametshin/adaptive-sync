#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using AdaptiveSync.Demo.Boot;
using AdaptiveSync.Demo.Boot.Steps;
using AdaptiveSync.Demo.ViewModels;
using AdaptiveSync.Demo.Views;
using UnityEngine;

namespace AdaptiveSync.Demo.App
{
    [DefaultExecutionOrder(-1000)]
    public sealed class AppEntryPoint : MonoBehaviour
    {
        public const float SettingsSeconds = 0.6f;
        public const float StorageSeconds = 0.9f;
        public const float ServerSeconds = 1.0f;
        public const float SceneSeconds = 1.0f;

        [SerializeField, Min(0f)] private float bootPace = 1f;

        private AppServices _services = null!;
        private Preloader _preloader = null!;
        private PreloaderViewModel _preloaderViewModel = null!;
        private bool _booting;

        public static AppEntryPoint? Instance { get; private set; }

        public static AppServices? Services => Instance == null ? null : Instance._services;

        public Preloader Preloader => _preloader;

        public PreloaderViewModel PreloaderViewModel => _preloaderViewModel;

        public BootResult? LastResult { get; private set; }

        public bool IsReady => _preloader != null && _preloader.IsComplete;

        public static float? PaceOverride { get; set; }

        public float BootPace => PaceOverride ?? bootPace;

        public static IEnumerable<IBootStep> CreateSteps(float pace = 1f)
        {
            pace = Mathf.Max(0f, pace);
            float Now() => Time.realtimeSinceStartup;
            return new IBootStep[]
            {
                new PacedStep(new LoadSettingsStep(), SettingsSeconds * pace, Now),
                new PacedStep(new OpenStorageStep(), StorageSeconds * pace, Now),
                new PacedStep(new CheckServerStep(), ServerSeconds * pace, Now),
                new LoadSceneStep(SceneNames.Main, SceneSeconds * pace),
            };
        }

        public static void Shutdown()
        {
            if (Instance != null) DestroyImmediate(Instance.gameObject);
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            _services = new AppServices();
            _preloader = new Preloader(CreateSteps(BootPace));
            _preloaderViewModel = new PreloaderViewModel(Retry);
            _preloader.ProgressChanged += OnProgress;
        }

        private void Start()
        {
            if (Instance != this) return;
            var view = FindAnyObjectByType<PreloaderView>();
            if (view != null) view.Bind(_preloaderViewModel);
            _ = BootAsync();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            _preloader.ProgressChanged -= OnProgress;
            _preloaderViewModel.Dispose();
            _services.Dispose();
            Instance = null;
        }

        private void Retry()
        {
            if (!_booting) _ = BootAsync();
        }

        private async Task BootAsync()
        {
            _booting = true;
            _preloaderViewModel.ShowRunning();
            try
            {
                LastResult = await _preloader.RunAsync(_services, destroyCancellationToken);
            }
            finally
            {
                _booting = false;
            }

            if (LastResult.Succeeded) return;
            Debug.LogException(LastResult.Error);
            _preloaderViewModel.ShowError(LastResult.FailedStep!.Title, LastResult.Error!);
        }

        private void OnProgress(BootProgress progress) => _preloaderViewModel.Apply(progress);
    }
}
