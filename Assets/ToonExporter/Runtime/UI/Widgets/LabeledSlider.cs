using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace ToonExporter.UI
{
    /// <summary>
    /// 数値直入力つきスライダー行（自前トラック方式）。
    /// </summary>
    public class LabeledSlider : VisualElement
    {
        public event Action<float> onChanged;

        private readonly VisualElement _slider;
        private readonly VisualElement _fill;
        private readonly VisualElement _thumb;
        private readonly FloatField _field;
        private readonly float _min;
        private readonly float _max;
        private readonly float _step;
        private float _value;
        private bool _isDragging;

        public LabeledSlider(string label, float min, float max, float step, float defaultValue = 0f)
        {
            _min = min;
            _max = max;
            _step = step;
            _value = Mathf.Clamp(defaultValue, min, max);

            AddToClassList("toon-row");
            AddToClassList("toon-row--slider");

            var lbl = new Label(label);
            lbl.AddToClassList("toon-row__label");
            Add(lbl);

            _slider = new VisualElement();
            _slider.AddToClassList("toon-slider");
            Add(_slider);

            var trackBg = new VisualElement();
            trackBg.AddToClassList("toon-slider__track-bg");
            _slider.Add(trackBg);

            _fill = new VisualElement();
            _fill.AddToClassList("toon-slider__fill");
            _slider.Add(_fill);

            _thumb = new VisualElement();
            _thumb.AddToClassList("toon-slider__thumb");
            _slider.Add(_thumb);

            _slider.RegisterCallback<PointerDownEvent>(OnSliderPointerDown);
            _slider.RegisterCallback<PointerMoveEvent>(OnSliderPointerMove);
            _slider.RegisterCallback<PointerUpEvent>(OnSliderPointerUp);

            _field = new FloatField();
            _field.AddToClassList("toon-num");
            _field.formatString = step >= 1f ? "F0" : step >= 0.01f ? "F2" : "F4";
            Add(_field);

            _field.RegisterValueChangedCallback(evt => Apply(evt.newValue, fromSlider: false));

            _field.SetValueWithoutNotify(_value);
            UpdateVisual();
        }

        private void OnSliderPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            _isDragging = true;
            _slider.CapturePointer(evt.pointerId);
            ApplyFromPointer(evt.localPosition);
            evt.StopPropagation();
        }

        private void OnSliderPointerMove(PointerMoveEvent evt)
        {
            if (!_isDragging) return;
            ApplyFromPointer(evt.localPosition);
            evt.StopPropagation();
        }

        private void OnSliderPointerUp(PointerUpEvent evt)
        {
            if (!_isDragging) return;
            _isDragging = false;
            _slider.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void ApplyFromPointer(Vector2 localPos)
        {
            var rect = _slider.contentRect;
            if (rect.width <= 0) return;
            float ratio = Mathf.Clamp01(localPos.x / rect.width);
            float v = _min + ratio * (_max - _min);
            Apply(v, fromSlider: true);
        }

        private void Apply(float v, bool fromSlider)
        {
            v = Mathf.Clamp(v, _min, _max);
            if (_step > 0f)
                v = Mathf.Round(v / _step) * _step;

            _value = v;
            _field.SetValueWithoutNotify(v);
            UpdateVisual();
            onChanged?.Invoke(v);
        }

        public void SetValueWithoutNotify(float v)
        {
            v = Mathf.Clamp(v, _min, _max);
            if (_step > 0f)
                v = Mathf.Round(v / _step) * _step;

            _value = v;
            _field.SetValueWithoutNotify(v);
            UpdateVisual();
        }

        public float Value => _value;

        private void UpdateVisual()
        {
            float range = _max - _min;
            float pct = range > 0f ? (_value - _min) / range * 100f : 0f;
            _fill.style.width = Length.Percent(pct);
            _thumb.style.left = Length.Percent(pct);
        }
    }
}
