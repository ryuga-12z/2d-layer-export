using System;
using UnityEngine.UIElements;

namespace ToonExporter.UI
{
    /// <summary>
    /// 折りたたみセクション。ヘッダ + caret + タイトル + トグル or ドット。
    /// MRT 固定スロットのコンテナとして使う。
    /// </summary>
    public class FoldableSection : VisualElement
    {
        public enum HeaderMode
        {
            Toggle,   // 通常スロット: 右に Enable トグル
            AutoDot,  // 自動算出ドット
            None      // トグル無し
        }

        public event Action<bool> onToggleChanged;

        private readonly VisualElement _header;
        private readonly Label _caret;
        private readonly VisualElement _body;
        private readonly ToggleSwitch _toggle;
        private readonly Label _autoDot;
        private bool _isOpen;
        private readonly HeaderMode _mode;

        public FoldableSection(string title, string subtitle,
            HeaderMode mode = HeaderMode.Toggle, bool initialOpen = false)
        {
            _mode = mode;
            _isOpen = initialOpen;

            AddToClassList("toon-section");

            _header = new VisualElement();
            _header.AddToClassList("toon-section__header");
            Add(_header);

            _caret = new Label(_isOpen ? "▼" : "▶");
            _caret.AddToClassList("toon-foldable__caret");
            _header.Add(_caret);

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("toon-section__title");
            _header.Add(titleLabel);

            if (!string.IsNullOrEmpty(subtitle))
            {
                var subLabel = new Label(subtitle);
                subLabel.AddToClassList("toon-section__subtitle");
                _header.Add(subLabel);
            }

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            _header.Add(spacer);

            switch (mode)
            {
                case HeaderMode.Toggle:
                    _toggle = new ToggleSwitch();
                    _header.Add(_toggle);
                    _toggle.onChanged += value => onToggleChanged?.Invoke(value);
                    break;

                case HeaderMode.AutoDot:
                    _autoDot = new Label("● 自動");
                    _autoDot.AddToClassList("toon-section__auto-dot");
                    _header.Add(_autoDot);
                    break;

                case HeaderMode.None:
                    break;
            }

            _header.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                // SetEnabled(false) されたトグルは ToggleSwitch 側の StopPropagation が
                // 働かず header まで素通りするので、トグル領域クリックでのセクション開閉を明示抑止
                if (_toggle != null && evt.target is VisualElement target &&
                    (target == _toggle || IsDescendantOfToggle(target)))
                {
                    evt.StopPropagation();
                    return;
                }
                ToggleFold();
                evt.StopPropagation();
            });

            _body = new VisualElement();
            _body.AddToClassList("toon-section__body");
            Add(_body);

            UpdateFoldVisual();
        }

        public void AddToBody(VisualElement child)
        {
            _body.Add(child);
        }

        public void ToggleFold()
        {
            _isOpen = !_isOpen;
            UpdateFoldVisual();
        }

        public void SetToggleWithoutNotify(bool value)
        {
            _toggle?.SetValueWithoutNotify(value);
        }

        public void SetAutoDotActive(bool enabled)
        {
            _autoDot?.EnableInClassList("toon-section__auto-dot--active", enabled);
        }

        public void SetBodyEnabled(bool enabled)
        {
            // 見た目のグレーアウトは USS クラスで、操作ブロックは SetEnabled で構造的に。
            // USS の picking-mode だけだと祖先 Ignore は子孫のクリックを潰さない穴があるので
            // SetEnabled(false) で enabledInHierarchy 経由のポインタイベント非対象化を保証する。
            _body.EnableInClassList("toon-section__body--disabled", !enabled);
            _body.SetEnabled(enabled);
        }

        public bool IsOpen => _isOpen;
        public ToggleSwitch Toggle => _toggle;

        private void UpdateFoldVisual()
        {
            _caret.text = _isOpen ? "▼" : "▶";
            _body.style.display = _isOpen ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // _toggle の子孫かどうか判定（ToggleSwitch は _track/_knob を内包）
        private bool IsDescendantOfToggle(VisualElement target)
        {
            var cur = target.parent;
            while (cur != null)
            {
                if (cur == _toggle) return true;
                cur = cur.parent;
            }
            return false;
        }
    }
}
