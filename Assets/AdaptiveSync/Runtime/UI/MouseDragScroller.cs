using UnityEngine;
using UnityEngine.UIElements;

namespace AdaptiveSync.Demo.UI
{
    public sealed class MouseDragScroller : PointerManipulator
    {
        private const float DragThreshold = 6f;

        private readonly ScrollView _scroll;
        private bool _pressed;
        private bool _dragging;
        private int _pointerId;
        private Vector2 _start;
        private Vector2 _startOffset;

        public MouseDragScroller(ScrollView scroll)
        {
            _scroll = scroll;
        }

        public bool IsDragging => _dragging;

        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            target.RegisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
            target.RegisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
            target.RegisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
        }

        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            target.UnregisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
            target.UnregisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
            target.UnregisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
        }

        private void OnPointerDown(PointerDownEvent e)
        {
            if (e.pointerType != UnityEngine.UIElements.PointerType.mouse || e.button != 0) return;
            _pressed = true;
            _dragging = false;
            _pointerId = e.pointerId;
            _start = e.position;
            _startOffset = _scroll.scrollOffset;
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            if (!_pressed || e.pointerId != _pointerId) return;
            var delta = (Vector2)e.position - _start;
            if (!_dragging)
            {
                if (Mathf.Abs(delta.y) < DragThreshold) return;
                _dragging = true;
                target.CapturePointer(_pointerId);
            }

            _scroll.scrollOffset = new Vector2(_startOffset.x, Mathf.Clamp(_startOffset.y - delta.y, 0f, MaxOffset(_scroll)));
            e.StopPropagation();
        }

        public static float MaxOffset(ScrollView scroll)
        {
            var content = scroll.contentContainer.layout.height;
            var viewport = scroll.contentViewport.layout.height;
            if (float.IsNaN(content) || float.IsNaN(viewport)) return 0f;
            return Mathf.Max(0f, content - viewport);
        }

        private void OnPointerUp(PointerUpEvent e)
        {
            if (!_pressed || e.pointerId != _pointerId) return;
            if (_dragging)
            {
                target.ReleasePointer(_pointerId);
                e.StopPropagation();
            }
            _pressed = false;
            _dragging = false;
        }

        private void OnCaptureOut(PointerCaptureOutEvent e)
        {
            _pressed = false;
            _dragging = false;
        }
    }
}
