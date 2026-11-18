using System;
using System.Collections.Generic;
using PsdUiWorkbench.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace PsdUiWorkbench.Presentation
{
    [UxmlElement]
    public sealed partial class PsdPreview : VisualElement
    {
        private readonly VisualElement _canvas = new VisualElement();
        private readonly VisualElement _outline = new VisualElement();
        private PsdDocumentSnapshot _document;
        private readonly Dictionary<int, Image> _images = new Dictionary<int, Image>();
        private float _zoom = 1;
        private Vector2 _pan;
        private Vector2 _lastPointer;
        private bool _panning;
        public event Action<float, float> Picked;
        public PsdPreview()
        {
            AddToClassList("preview");
            focusable = true;
            _canvas.AddToClassList("canvas");
            _outline.AddToClassList("selection-outline");
            _outline.pickingMode = PickingMode.Ignore;
            Add(_canvas);
            RegisterCallback<WheelEvent>(evt =>
            {
                Vector2 point = evt.localMousePosition;
                float next = Mathf.Clamp(_zoom * Mathf.Exp(-evt.delta.y * 0.08f), 0.02f, 8f);
                _pan = point - (point - _pan) * (next / _zoom);
                _zoom = next; TransformCanvas(); evt.StopPropagation();
            });
            RegisterCallback<PointerDownEvent>(evt =>
            {
                Focus();
                if (evt.button == 2 || evt.button == 1)
                {
                    _panning = true; _lastPointer = evt.localPosition; this.CapturePointer(evt.pointerId);
                }
                else if (evt.button == 0 && _document != null)
                {
                    var p = ((Vector2)evt.localPosition - _pan) / _zoom;
                    Picked?.Invoke(p.x, p.y);
                }
            });
            RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!_panning) return;
                _pan += (Vector2)evt.localPosition - _lastPointer;
                _lastPointer = evt.localPosition; TransformCanvas();
            });
            RegisterCallback<PointerUpEvent>(evt => { _panning = false; this.ReleasePointer(evt.pointerId); });
            RegisterCallback<PointerCaptureOutEvent>(_ => _panning = false);
            generateVisualContent += DrawCheckerboard;
        }
        private void DrawCheckerboard(MeshGenerationContext context)
        {
            var painter = context.painter2D;
            const int cell = 20;
            for (int y = 0; y < contentRect.height; y += cell)
                for (int x = 0; x < contentRect.width; x += cell)
                {
                    painter.fillColor = ((x / cell + y / cell) % 2 == 0) ? new Color(.16f, .16f, .16f) : new Color(.21f, .21f, .21f);
                    painter.BeginPath(); painter.MoveTo(new Vector2(x, y)); painter.LineTo(new Vector2(x + cell, y));
                    painter.LineTo(new Vector2(x + cell, y + cell)); painter.LineTo(new Vector2(x, y + cell));
                    painter.ClosePath(); painter.Fill();
                }
        }
        public void SetDocument(PsdDocumentSnapshot document, Func<string, Sprite> resolve)
        {
            _document = document; _canvas.Clear(); _images.Clear();
            if (document == null) return;
            _canvas.style.width = document.Width; _canvas.style.height = document.Height;
            foreach (var node in document.DrawNodes)
            {
                var image = new Image { sprite = resolve(node.SpriteId), scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
                image.AddToClassList("layer-image"); Place(image, node.Bounds); _canvas.Add(image);
                _images.Add(node.Id, image);
            }
            _canvas.Add(_outline); _outline.style.display = DisplayStyle.None; TransformCanvas();
        }
        public void Fit()
        {
            if (_document == null || contentRect.width <= 0 || contentRect.height <= 0) return;
            _zoom = Mathf.Max(.02f, Mathf.Min((contentRect.width - 32) / _document.Width, (contentRect.height - 32) / _document.Height));
            _pan = new Vector2((contentRect.width - _document.Width * _zoom) / 2, (contentRect.height - _document.Height * _zoom) / 2);
            TransformCanvas();
        }
        public bool Frame(PixelRect bounds)
        {
            var viewport = contentRect;
            if (_document == null || !bounds.IsValid || !IsFinite(bounds.X) || !IsFinite(bounds.Y) ||
                !IsFinite(bounds.Width) || !IsFinite(bounds.Height) || !IsFinite(viewport.width) ||
                !IsFinite(viewport.height) || viewport.width <= 0 || viewport.height <= 0) return false;
            const float margin = 24;
            float next = Mathf.Min(Mathf.Max(1, viewport.width - margin * 2) / bounds.Width,
                Mathf.Max(1, viewport.height - margin * 2) / bounds.Height);
            if (!IsFinite(next)) return false;
            float zoom = Mathf.Clamp(next, 0.02f, 8f);
            var pan = viewport.center - new Vector2(bounds.X + bounds.Width / 2,
                bounds.Y + bounds.Height / 2) * zoom;
            if (!IsFinite(pan.x) || !IsFinite(pan.y)) return false;
            _zoom = zoom;
            _pan = pan;
            TransformCanvas();
            return true;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private void TransformCanvas()
        {
            _canvas.style.left = _pan.x; _canvas.style.top = _pan.y;
            _canvas.style.scale = new Scale(new Vector3(_zoom, _zoom, 1));
        }
        public void UpdateVisibility(PsdSession session)
        {
            foreach (var pair in _images)
                pair.Value.style.display = session.IsEffectivelyVisible(pair.Key) ? DisplayStyle.Flex : DisplayStyle.None;
            var rect = session.SelectedId.HasValue ? session.GetVisibleBounds(session.SelectedId.Value) : default;
            _outline.style.display = rect.IsValid ? DisplayStyle.Flex : DisplayStyle.None;
            if (rect.IsValid) Place(_outline, rect);
        }
        private static void Place(VisualElement element, PixelRect rect)
        {
            element.style.left = rect.X; element.style.top = rect.Y;
            element.style.width = rect.Width; element.style.height = rect.Height;
        }
    }
}
