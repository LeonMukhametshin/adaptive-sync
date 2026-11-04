#nullable enable
using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace AdaptiveSync.Demo.UI
{
    public static class UiKit
    {
        public static T Find<T>(VisualElement root, string name) where T : VisualElement =>
            root.Q<T>(name) ?? throw new InvalidOperationException("UI element '" + name + "' is missing in the layout.");

        public static Label Text(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }

        public static Label Badge(string text, string modifier)
        {
            var label = Text(text, "badge");
            label.AddToClassList(modifier);
            return label;
        }

        public static VisualElement Row(params string[] classNames)
        {
            var row = new VisualElement();
            row.AddToClassList("row");
            foreach (var className in classNames) row.AddToClassList(className);
            return row;
        }

        public static VisualElement Card()
        {
            var card = new VisualElement();
            card.AddToClassList("card");
            return card;
        }

        public static void SetVisible(VisualElement element, bool visible) =>
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        public static void OnTap(VisualElement element, Action action)
        {
            const float maxMoveSquared = 100f;
            var start = Vector2.zero;
            var pressed = false;
            element.RegisterCallback<PointerDownEvent>(e =>
            {
                pressed = true;
                start = e.position;
                element.AddToClassList("card--pressed");
            });
            element.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (pressed && ((Vector2)e.position - start).sqrMagnitude > maxMoveSquared)
                {
                    pressed = false;
                    element.RemoveFromClassList("card--pressed");
                }
            });
            element.RegisterCallback<PointerUpEvent>(e =>
            {
                var tapped = pressed && ((Vector2)e.position - start).sqrMagnitude <= maxMoveSquared;
                pressed = false;
                element.RemoveFromClassList("card--pressed");
                if (tapped) action();
            });
            element.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                pressed = false;
                element.RemoveFromClassList("card--pressed");
            });
        }

        public static void ConfigureMobileScroll(ScrollView scroll)
        {
            scroll.mode = ScrollViewMode.Vertical;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.touchScrollBehavior = ScrollView.TouchScrollBehavior.Elastic;
            scroll.elasticity = 0.12f;
            scroll.scrollDecelerationRate = 0.135f;
            scroll.mouseWheelScrollSize = 80f;
            scroll.AddManipulator(new MouseDragScroller(scroll));
        }
    }
}
