using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace ToonExporter.UI
{
    /// <summary>
    /// 2D 座標パッド（148x148）。Y は上方向が +。
    /// ライト方向指定に使う（0..1 + Y 上反転が N・L に直撃）。
    /// </summary>
    public class Vector2Pad : VisualElement
    {
        public event Action<Vector2> onChanged;

        private readonly VisualElement _pad;
        private readonly VisualElement _dot;
        private readonly FloatField _fieldX;
        private readonly FloatField _fieldY;
        private Vector2 _value = new(0.5f, 0.5f);
        private bool _isDragging;

        public Vector2Pad(string label)
        {
            AddToClassList("toon-row--point");

            var topRow = new VisualElement();
            topRow.AddToClassList("toon-pad__top-row");
            Add(topRow);

            var lbl = new Label(label);
            lbl.AddToClassList("toon-row__label");
            topRow.Add(lbl);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            topRow.Add(spacer);

            var xLabel = new Label("X");
            xLabel.AddToClassList("toon-pad__axis-label");
            topRow.Add(xLabel);

            _fieldX = new FloatField();
            _fieldX.AddToClassList("toon-pad__field");
            _fieldX.formatString = "F2";
            topRow.Add(_fieldX);

            var yLabel = new Label("Y");
            yLabel.AddToClassList("toon-pad__axis-label");
            topRow.Add(yLabel);

            _fieldY = new FloatField();
            _fieldY.AddToClassList("toon-pad__field");
            _fieldY.formatString = "F2";
            topRow.Add(_fieldY);

            _pad = new VisualElement();
            _pad.AddToClassList("toon-pad");
            Add(_pad);

            _dot = new VisualElement();
            _dot.AddToClassList("toon-pad__dot");
            _pad.Add(_dot);

            _pad.RegisterCallback<PointerDownEvent>(OnPadPointerDown);
            _pad.RegisterCallback<PointerMoveEvent>(OnPadPointerMove);
            _pad.RegisterCallback<PointerUpEvent>(OnPadPointerUp);

            _fieldX.RegisterValueChangedCallback(evt =>
            {
                float x = Mathf.Clamp01(evt.newValue);
                _fieldX.SetValueWithoutNotify(x);
                SetInternal(new Vector2(x, _value.y), notifyFields: false);
            });
            _fieldY.RegisterValueChangedCallback(evt =>
            {
                float y = Mathf.Clamp01(evt.newValue);
                _fieldY.SetValueWithoutNotify(y);
                SetInternal(new Vector2(_value.x, y), notifyFields: false);
            });

            UpdateVisual();
            _fieldX.SetValueWithoutNotify(_value.x);
            _fieldY.SetValueWithoutNotify(_value.y);
        }

        private void OnPadPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            _isDragging = true;
            _pad.CapturePointer(evt.pointerId);
            ApplyPointerPosition(evt.localPosition);
            evt.StopPropagation();
        }

        private void OnPadPointerMove(PointerMoveEvent evt)
        {
            if (!_isDragging) return;
            ApplyPointerPosition(evt.localPosition);
            evt.StopPropagation();
        }

        private void OnPadPointerUp(PointerUpEvent evt)
        {
            if (!_isDragging) return;
            _isDragging = false;
            _pad.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void ApplyPointerPosition(Vector2 localPos)
        {
            var rect = _pad.contentRect;
            if (rect.width <= 0 || rect.height <= 0) return;

            float x = Mathf.Clamp01(localPos.x / rect.width);
            float y = Mathf.Clamp01(1f - localPos.y / rect.height);
            SetInternal(new Vector2(x, y), notifyFields: true);
        }

        private void SetInternal(Vector2 v, bool notifyFields)
        {
            _value = v;
            UpdateVisual();

            if (notifyFields)
            {
                _fieldX.SetValueWithoutNotify(v.x);
                _fieldY.SetValueWithoutNotify(v.y);
            }

            onChanged?.Invoke(v);
        }

        public void SetValueWithoutNotify(Vector2 v)
        {
            _value = new Vector2(Mathf.Clamp01(v.x), Mathf.Clamp01(v.y));
            UpdateVisual();
            _fieldX.SetValueWithoutNotify(_value.x);
            _fieldY.SetValueWithoutNotify(_value.y);
        }

        public Vector2 Value => _value;

        private void UpdateVisual()
        {
            _dot.style.left = Length.Percent(_value.x * 100f);
            _dot.style.top = Length.Percent((1f - _value.y) * 100f);
        }
    }
}
