using System;
using UnityEngine.UIElements;

namespace ToonExporter.UI
{
    /// <summary>
    /// ピル型トグルスイッチ。
    /// セクションヘッダに置く場合、折りたたみへの伝播を StopPropagation で防ぐ。
    /// </summary>
    public class ToggleSwitch : VisualElement
    {
        public event Action<bool> onChanged;

        private readonly VisualElement _track;
        private readonly VisualElement _knob;
        private bool _value;

        public ToggleSwitch(bool isSubSize = false)
        {
            AddToClassList("toon-toggle");
            if (isSubSize)
                AddToClassList("toon-toggle--sub");

            _track = new VisualElement();
            _track.AddToClassList("toon-toggle__track");
            Add(_track);

            _knob = new VisualElement();
            _knob.AddToClassList("toon-toggle__knob");
            _track.Add(_knob);

            _track.RegisterCallback<PointerDownEvent>(evt =>
            {
                evt.StopPropagation();
                SetValue(!_value);
            });

            UpdateVisual();
        }

        public void SetValue(bool value)
        {
            _value = value;
            UpdateVisual();
            onChanged?.Invoke(_value);
        }

        public void SetValueWithoutNotify(bool value)
        {
            _value = value;
            UpdateVisual();
        }

        public bool Value => _value;

        private void UpdateVisual()
        {
            EnableInClassList("toon-toggle--on", _value);
        }
    }
}
