#nullable enable
using AdaptiveSync.Demo.Mvvm;
using AdaptiveSync.Demo.UI;
using AdaptiveSync.Demo.ViewModels;
using UnityEngine;
using UnityEngine.UIElements;

namespace AdaptiveSync.Demo.Views
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class PreloaderView : MonoBehaviour
    {
        private readonly CompositeDisposable _bindings = new CompositeDisposable();
        private VisualElement? _root;
        private SafeArea? _safeArea;
        private string? _fatalError;

        public VisualElement? Root => _root;

        private void OnEnable()
        {
            var document = GetComponent<UIDocument>();
            if (document.panelSettings == null || document.visualTreeAsset == null)
            {
                _fatalError = "UIDocument прелоадера без PanelSettings или разметки.";
                return;
            }

            _root = document.rootVisualElement;
            _safeArea = new SafeArea(_root, UiKit.Find<VisualElement>(_root, "app"));
        }

        public void Bind(PreloaderViewModel viewModel)
        {
            if (_root == null) return;
            var fill = UiKit.Find<VisualElement>(_root, "boot-progress-fill");
            _bindings.Add(Views.Bind.Text(UiKit.Find<Label>(_root, "boot-step"), viewModel.StepText));
            _bindings.Add(Views.Bind.Text(UiKit.Find<Label>(_root, "boot-percent"), viewModel.PercentText));
            _bindings.Add(viewModel.Progress.Subscribe(value => fill.style.width = Length.Percent(value * 100f)));
            _bindings.Add(Views.Bind.Class(UiKit.Find<VisualElement>(_root, "boot-error"), "boot__error--visible", viewModel.HasError));
            _bindings.Add(Views.Bind.Text(UiKit.Find<Label>(_root, "boot-error-text"), viewModel.ErrorText));
            _bindings.Add(Views.Bind.Command(UiKit.Find<Button>(_root, "boot-retry"), viewModel.RetryCommand));
        }

        private void Update() => _safeArea?.Apply();

        private void OnDisable() => _bindings.Dispose();

        private void OnGUI()
        {
            if (_fatalError != null) GUI.Label(new Rect(20, 20, Screen.width - 40, Screen.height - 40), _fatalError);
        }
    }
}
