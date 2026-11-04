using UnityEngine;
using UnityEngine.UIElements;

namespace AdaptiveSync.Demo.UI
{
    public sealed class SafeArea
    {
        private readonly VisualElement _panelRoot;
        private readonly VisualElement _target;
        private Rect _applied;

        public SafeArea(VisualElement panelRoot, VisualElement target)
        {
            _panelRoot = panelRoot;
            _target = target;
        }

        public void Apply()
        {
            var safe = Screen.safeArea;
            if (safe == _applied || Screen.width == 0 || Screen.height == 0) return;
            var panelWidth = _panelRoot.resolvedStyle.width;
            var panelHeight = _panelRoot.resolvedStyle.height;
            if (float.IsNaN(panelWidth) || panelWidth <= 0) return;

            _applied = safe;
            var scaleX = panelWidth / Screen.width;
            var scaleY = panelHeight / Screen.height;
            _target.style.paddingLeft = safe.xMin * scaleX;
            _target.style.paddingRight = (Screen.width - safe.xMax) * scaleX;
            _target.style.paddingTop = (Screen.height - safe.yMax) * scaleY;
            _target.style.paddingBottom = safe.yMin * scaleY;
        }
    }
}
