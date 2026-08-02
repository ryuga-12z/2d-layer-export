using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace ToonExporter.UI
{
    /// <summary>
    /// 択一セグメントコントロール。
    /// </summary>
    public class SegmentedControl : VisualElement
    {
        public event Action<int> onChanged;

        private readonly List<Button> _buttons = new();
        private int _selectedIndex = -1;

        public SegmentedControl(string label, string[] options)
        {
            AddToClassList("toon-row");
            AddToClassList("toon-row--segment");

            var lbl = new Label(label);
            lbl.AddToClassList("toon-row__label");
            Add(lbl);

            var container = new VisualElement();
            container.AddToClassList("toon-seg");
            Add(container);

            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                var btn = new Button(() => Select(idx)) { text = options[i] };
                btn.AddToClassList("toon-seg__btn");
                _buttons.Add(btn);
                container.Add(btn);
            }
        }

        public void Select(int index)
        {
            if (index < 0 || index >= _buttons.Count) return;

            _selectedIndex = index;
            for (int k = 0; k < _buttons.Count; k++)
                _buttons[k].EnableInClassList("toon-seg__btn--active", k == index);

            onChanged?.Invoke(index);
        }

        public void SetValueWithoutNotify(int index)
        {
            if (index < 0 || index >= _buttons.Count) return;

            _selectedIndex = index;
            for (int k = 0; k < _buttons.Count; k++)
                _buttons[k].EnableInClassList("toon-seg__btn--active", k == index);
        }

        public int SelectedIndex => _selectedIndex;
    }
}
