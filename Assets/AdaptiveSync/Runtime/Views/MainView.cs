#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AdaptiveSync.Demo.App;
using AdaptiveSync.Demo.Mvvm;
using AdaptiveSync.Demo.Services;
using AdaptiveSync.Demo.UI;
using AdaptiveSync.Demo.ViewModels;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace AdaptiveSync.Demo.Views
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class MainView : MonoBehaviour
    {
        private readonly CompositeDisposable _bindings = new CompositeDisposable();
        private VisualElement _root = null!;
        private MainViewModel? _viewModel;
        private SessionService? _sessions;
        private SafeArea? _safeArea;
        private VisualElement? _spinner;
        private Label? _toast;
        private float _toastHiddenAt;
        private string? _fatalError;
        private GUIStyle? _errorStyle;

        public VisualElement Root => _root;

        public MainViewModel? ViewModel => _viewModel;

        public SyncDemoSession? Session => _viewModel == null ? null : _sessions!.Current;

        public bool IsReady => _viewModel != null;

        public bool IsBusy => _viewModel != null && _viewModel.IsBusy;

        public string? FatalError => _fatalError;

        private void OnEnable()
        {
            var services = AppEntryPoint.Services;
            if (services == null || !services.Has<SessionService>())
            {
                SceneManager.LoadScene(SceneNames.Boot);
                return;
            }

            try
            {
                var document = GetComponent<UIDocument>();
                if (document.panelSettings == null)
                    throw new InvalidOperationException("UIDocument has no PanelSettings: the interface cannot be drawn.");
                if (document.visualTreeAsset == null)
                    throw new InvalidOperationException("UIDocument has no layout (MainScreen.uxml).");

                _root = document.rootVisualElement;
                _sessions = services.Get<SessionService>();
                var viewModel = new MainViewModel(_sessions, services.Get<ServerStatus>());
                BindChrome(viewModel);
                _bindings.Add(new ProfilesView(_root, viewModel.Profiles));
                _bindings.Add(new SyncView(_root, viewModel.Sync));
                _bindings.Add(new JournalView(_root, viewModel.Journal));
                _bindings.Add(new SettingsView(_root, viewModel.Settings));
                _bindings.Add(viewModel);
                _viewModel = viewModel;
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private void OnDisable()
        {
            _bindings.Dispose();
            _viewModel = null;
        }

        private void Update()
        {
            if (_viewModel == null) return;
            _safeArea?.Apply();
            if (_viewModel.IsBusy && _spinner != null)
                _spinner.style.rotate = new Rotate(new Angle(Time.unscaledTime * 360f % 360f, AngleUnit.Degree));
            if (_toastHiddenAt > 0 && Time.unscaledTime >= _toastHiddenAt && _toast != null)
            {
                _toastHiddenAt = 0;
                _toast.RemoveFromClassList("toast--visible");
            }
        }

        public void SelectTab(string tab) => _viewModel?.SelectTabCommand.Execute(tab);

        public Task SynchronizeAsync() => _viewModel?.Sync.SyncCommand.ExecuteAsync() ?? Task.CompletedTask;

        private void BindChrome(MainViewModel viewModel)
        {
            _safeArea = new SafeArea(_root, UiKit.Find<VisualElement>(_root, "app"));
            _spinner = UiKit.Find<VisualElement>(_root, "sync-spinner");

            _bindings.Add(Bind.Text(UiKit.Find<Label>(_root, "client-label"), viewModel.ClientLabel));
            var pill = UiKit.Find<Button>(_root, "network-pill");
            _bindings.Add(Bind.Text(pill, viewModel.NetworkText));
            _bindings.Add(Bind.Class(pill, "pill--online", viewModel.IsOnline));
            _bindings.Add(viewModel.IsOnline.Subscribe(online => pill.EnableInClassList("pill--offline", !online)));
            _bindings.Add(Bind.Command(pill, viewModel.ToggleOnlineCommand));

            var badge = UiKit.Find<Label>(_root, "tab-sync-badge");
            _bindings.Add(Bind.Text(badge, viewModel.PendingBadge));
            _bindings.Add(Bind.Class(badge, "tab__badge--visible", viewModel.HasPending));

            foreach (var tab in MainViewModel.Tabs)
            {
                var name = tab;
                var selected = viewModel.IsTabSelected(name);
                var page = UiKit.Find<ScrollView>(_root, "page-" + name);
                var button = UiKit.Find<Button>(_root, "tab-" + name);
                UiKit.ConfigureMobileScroll(page);
                _bindings.Add(Bind.Class(page, "page--active", selected));
                _bindings.Add(Bind.Class(button, "tab--active", selected));
                Action select = () => viewModel.SelectTabCommand.Execute(name);
                button.clicked += select;
                _bindings.Add(new Subscription(() => button.clicked -= select));
            }

            _toast = UiKit.Find<Label>(_root, "toast");
            _bindings.Add(viewModel.Toast.Subscribe(ShowToast, invokeNow: false));
        }

        private void ShowToast(ToastMessage? message)
        {
            if (message == null || _toast == null) return;
            _toast.text = message.Text;
            _toast.EnableInClassList("toast--error", message.IsError);
            _toast.AddToClassList("toast--visible");
            _toastHiddenAt = Time.unscaledTime + (message.IsError ? 5f : 2.5f);
        }

        private void Fail(Exception ex)
        {
            _fatalError = ex.GetType().Name + ": " + ex.Message +
                          (ex.InnerException == null ? "" : "\n" + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
            Debug.LogException(ex);
        }

        private void OnGUI()
        {
            if (_fatalError == null) return;
            _errorStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Max(28, Screen.width / 28),
                wordWrap = true,
                normal = { textColor = new Color(1f, 0.55f, 0.55f) },
            };
            var margin = Screen.width * 0.05f;
            GUI.Label(new Rect(margin, Screen.height * 0.1f, Screen.width - margin * 2, Screen.height * 0.8f),
                "Adaptive Sync не запустился\n\n" + _fatalError, _errorStyle);
        }
    }
}
