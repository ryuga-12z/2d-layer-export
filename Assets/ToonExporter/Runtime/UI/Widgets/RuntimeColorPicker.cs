using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace ToonExporter.UI
{
    /// <summary>
    /// ランタイム用カラーピッカー（ポップオーバー形式）。
    /// generateVisualContent でテクスチャ不要のメッシュ描画。
    /// </summary>
    public class RuntimeColorPicker : VisualElement
    {
        private const float SvWidth = 186f;
        private const float SvHeight = 130f;
        private const float HueWidth = 186f;
        private const float HueHeight = 14f;

        public event Action<Color> onChanged;

        private float _h;
        private float _s;
        private float _v;

        private readonly VisualElement _svPlane;
        private readonly VisualElement _svKnob;
        private readonly VisualElement _huePlane;
        private readonly VisualElement _hueKnob;
        private readonly TextField _hexField;
        private readonly Button _doneBtn;

        private bool _svDragging;
        private bool _hueDragging;
        private string _lastValidHex;

        private EventCallback<PointerDownEvent> _outsideClickHandler;
        private VisualElement _appRoot;

        public RuntimeColorPicker()
        {
            name = "toon-colorpicker";
            AddToClassList("toon-colorpicker");
            style.display = DisplayStyle.None;
            style.position = Position.Absolute;
            pickingMode = PickingMode.Position;

            // SV 平面
            _svPlane = new VisualElement();
            _svPlane.AddToClassList("toon-cp__sv");
            _svPlane.generateVisualContent += OnGenerateSvContent;
            Add(_svPlane);

            _svKnob = new VisualElement();
            _svKnob.AddToClassList("toon-cp__sv-knob");
            _svPlane.Add(_svKnob);

            _svPlane.RegisterCallback<PointerDownEvent>(OnSvPointerDown);
            _svPlane.RegisterCallback<PointerMoveEvent>(OnSvPointerMove);
            _svPlane.RegisterCallback<PointerUpEvent>(OnSvPointerUp);

            // Hue 帯
            _huePlane = new VisualElement();
            _huePlane.AddToClassList("toon-cp__hue");
            _huePlane.generateVisualContent += OnGenerateHueContent;
            Add(_huePlane);

            _hueKnob = new VisualElement();
            _hueKnob.AddToClassList("toon-cp__hue-knob");
            _huePlane.Add(_hueKnob);

            _huePlane.RegisterCallback<PointerDownEvent>(OnHuePointerDown);
            _huePlane.RegisterCallback<PointerMoveEvent>(OnHuePointerMove);
            _huePlane.RegisterCallback<PointerUpEvent>(OnHuePointerUp);

            // HEX 入力行
            var hexRow = new VisualElement();
            hexRow.AddToClassList("toon-cp__hex-row");
            Add(hexRow);

            var hashLabel = new Label("#");
            hashLabel.AddToClassList("toon-cp__hash");
            hexRow.Add(hashLabel);

            _hexField = new TextField();
            _hexField.AddToClassList("toon-cp__hex-field");
            _hexField.maxLength = 6;
            hexRow.Add(_hexField);

            _hexField.RegisterCallback<KeyDownEvent>(OnHexKeyDown);
            _hexField.RegisterCallback<FocusOutEvent>(OnHexFocusOut);

            // 完了ボタン
            _doneBtn = new Button(Close);
            _doneBtn.text = "完了";
            _doneBtn.AddToClassList("toon-cp__done-btn");
            Add(_doneBtn);
        }

        // ================================================================
        //  公開 API
        // ================================================================

        public void Open(Color color, Vector2 anchorScreenPos, VisualElement appRoot)
        {
            if (style.display == DisplayStyle.Flex) Close();

            Color.RGBToHSV(color, out _h, out _s, out _v);
            _lastValidHex = ColorToHex(color);
            _appRoot = appRoot;

            style.display = DisplayStyle.Flex;
            _hexField.SetValueWithoutNotify(_lastValidHex);
            UpdateKnobPositions();

            _svPlane.MarkDirtyRepaint();
            _huePlane.MarkDirtyRepaint();

            RegisterCallback<GeometryChangedEvent>(OnGeometryForPlacement);

            style.left = anchorScreenPos.x;
            style.top = anchorScreenPos.y;

            RegisterOutsideClickHandler();
        }

        public void Close()
        {
            style.display = DisplayStyle.None;
            UnregisterCallback<GeometryChangedEvent>(OnGeometryForPlacement);
            UnregisterOutsideClickHandler();
        }

        public Color CurrentColor => Color.HSVToRGB(_h, _s, _v);

        // ================================================================
        //  配置クランプ
        // ================================================================

        private void OnGeometryForPlacement(GeometryChangedEvent evt)
        {
            UnregisterCallback<GeometryChangedEvent>(OnGeometryForPlacement);
            ClampToScreen();
        }

        private void ClampToScreen()
        {
            if (_appRoot == null) return;

            var appRect = _appRoot.contentRect;
            float pickerW = resolvedStyle.width;
            float pickerH = resolvedStyle.height;
            if (float.IsNaN(pickerW) || pickerW <= 0) return;

            float left = resolvedStyle.left;
            float top = resolvedStyle.top;

            float maxLeft = appRect.width - pickerW - 4f;
            float maxTop = appRect.height - pickerH - 4f;

            left = Mathf.Clamp(left, 4f, Mathf.Max(4f, maxLeft));
            top = Mathf.Clamp(top, 4f, Mathf.Max(4f, maxTop));

            style.left = left;
            style.top = top;
        }

        // ================================================================
        //  外部クリックで閉じる
        // ================================================================

        private void RegisterOutsideClickHandler()
        {
            if (_appRoot == null) return;

            _outsideClickHandler = evt =>
            {
                var target = evt.target as VisualElement;
                if (target != null && (target == this || IsDescendant(target)))
                    return;
                Close();
            };
            _appRoot.RegisterCallback(_outsideClickHandler, TrickleDown.TrickleDown);
        }

        private void UnregisterOutsideClickHandler()
        {
            if (_appRoot != null && _outsideClickHandler != null)
            {
                _appRoot.UnregisterCallback(_outsideClickHandler, TrickleDown.TrickleDown);
                _outsideClickHandler = null;
            }
        }

        private bool IsDescendant(VisualElement target)
        {
            var current = target;
            while (current != null)
            {
                if (current == this) return true;
                current = current.parent;
            }
            return false;
        }

        // ================================================================
        //  SV 平面 — generateVisualContent
        // ================================================================

        private void OnGenerateSvContent(MeshGenerationContext mgc)
        {
            var rect = _svPlane.contentRect;
            if (rect.width <= 0 || rect.height <= 0) return;

            float w = rect.width;
            float h = rect.height;

            Color baseColor = Color.HSVToRGB(_h, 1f, 1f);
            DrawQuad(mgc, 0, 0, w, h, baseColor, baseColor, baseColor, baseColor);

            Color wL = new Color(1f, 1f, 1f, 1f);
            Color wR = new Color(1f, 1f, 1f, 0f);
            DrawQuad(mgc, 0, 0, w, h, wL, wR, wR, wL);

            Color bT = new Color(0f, 0f, 0f, 0f);
            Color bB = new Color(0f, 0f, 0f, 1f);
            DrawQuad(mgc, 0, 0, w, h, bT, bT, bB, bB);
        }

        // ================================================================
        //  Hue 帯
        // ================================================================

        private void OnGenerateHueContent(MeshGenerationContext mgc)
        {
            var rect = _huePlane.contentRect;
            if (rect.width <= 0 || rect.height <= 0) return;

            float w = rect.width;
            float h = rect.height;

            Color[] hueKeys = new Color[]
            {
                new(1, 0, 0, 1), new(1, 1, 0, 1), new(0, 1, 0, 1),
                new(0, 1, 1, 1), new(0, 0, 1, 1), new(1, 0, 1, 1),
                new(1, 0, 0, 1),
            };

            float segW = w / 6f;
            for (int i = 0; i < 6; i++)
            {
                float x0 = segW * i;
                float x1 = segW * (i + 1);
                DrawQuad(mgc, x0, 0, x1 - x0, h, hueKeys[i], hueKeys[i + 1], hueKeys[i + 1], hueKeys[i]);
            }
        }

        // ================================================================
        //  Quad ヘルパー
        // ================================================================

        private static void DrawQuad(
            MeshGenerationContext mgc,
            float x, float y, float w, float h,
            Color topLeft, Color topRight, Color bottomRight, Color bottomLeft)
        {
            var mesh = mgc.Allocate(4, 6);
            if (mesh.vertexCount == 0) return;

            mesh.SetNextVertex(new Vertex { position = new Vector3(x, y, Vertex.nearZ), tint = topLeft });
            mesh.SetNextVertex(new Vertex { position = new Vector3(x + w, y, Vertex.nearZ), tint = topRight });
            mesh.SetNextVertex(new Vertex { position = new Vector3(x + w, y + h, Vertex.nearZ), tint = bottomRight });
            mesh.SetNextVertex(new Vertex { position = new Vector3(x, y + h, Vertex.nearZ), tint = bottomLeft });

            mesh.SetNextIndex(0);
            mesh.SetNextIndex(1);
            mesh.SetNextIndex(2);
            mesh.SetNextIndex(0);
            mesh.SetNextIndex(2);
            mesh.SetNextIndex(3);
        }

        // ================================================================
        //  SV ドラッグ
        // ================================================================

        private void OnSvPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            _svDragging = true;
            _svPlane.CapturePointer(evt.pointerId);
            ApplySvFromPointer(evt.localPosition);
            evt.StopPropagation();
        }

        private void OnSvPointerMove(PointerMoveEvent evt)
        {
            if (!_svDragging) return;
            ApplySvFromPointer(evt.localPosition);
            evt.StopPropagation();
        }

        private void OnSvPointerUp(PointerUpEvent evt)
        {
            if (!_svDragging) return;
            _svDragging = false;
            _svPlane.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void ApplySvFromPointer(Vector2 localPos)
        {
            var rect = _svPlane.contentRect;
            if (rect.width <= 0 || rect.height <= 0) return;
            _s = Mathf.Clamp01(localPos.x / rect.width);
            _v = Mathf.Clamp01(1f - localPos.y / rect.height);
            Recompute();
        }

        // ================================================================
        //  Hue ドラッグ
        // ================================================================

        private void OnHuePointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            _hueDragging = true;
            _huePlane.CapturePointer(evt.pointerId);
            ApplyHueFromPointer(evt.localPosition);
            evt.StopPropagation();
        }

        private void OnHuePointerMove(PointerMoveEvent evt)
        {
            if (!_hueDragging) return;
            ApplyHueFromPointer(evt.localPosition);
            evt.StopPropagation();
        }

        private void OnHuePointerUp(PointerUpEvent evt)
        {
            if (!_hueDragging) return;
            _hueDragging = false;
            _huePlane.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void ApplyHueFromPointer(Vector2 localPos)
        {
            var rect = _huePlane.contentRect;
            if (rect.width <= 0) return;
            _h = Mathf.Clamp01(localPos.x / rect.width);
            _svPlane.MarkDirtyRepaint();
            Recompute();
        }

        // ================================================================
        //  HEX 入力
        // ================================================================

        private void OnHexKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                TryApplyHex();
                evt.StopPropagation();
            }
        }

        private void OnHexFocusOut(FocusOutEvent evt)
        {
            TryApplyHex();
        }

        private void TryApplyHex()
        {
            string raw = _hexField.value?.Trim() ?? "";
            if (raw.StartsWith("#"))
                raw = raw.Substring(1);

            if (raw.Length == 6 && IsValidHex(raw))
            {
                Color parsed = HexToColor(raw);
                Color.RGBToHSV(parsed, out _h, out _s, out _v);
                _lastValidHex = raw.ToUpper();

                UpdateKnobPositions();
                _svPlane.MarkDirtyRepaint();
                _huePlane.MarkDirtyRepaint();

                onChanged?.Invoke(CurrentColor);
            }
            else
            {
                _hexField.SetValueWithoutNotify(_lastValidHex);
            }
        }

        // ================================================================
        //  HSV → 全 UI 更新
        // ================================================================

        private void Recompute()
        {
            var c = Color.HSVToRGB(_h, _s, _v);
            UpdateKnobPositions();
            _lastValidHex = ColorToHex(c);
            _hexField.SetValueWithoutNotify(_lastValidHex);
            onChanged?.Invoke(c);
        }

        private void UpdateKnobPositions()
        {
            _svKnob.style.left = Length.Percent(_s * 100f);
            _svKnob.style.top = Length.Percent((1f - _v) * 100f);
            _hueKnob.style.left = Length.Percent(_h * 100f);
        }

        // ================================================================
        //  色変換ユーティリティ
        // ================================================================

        /// <summary>Color → "RRGGBB" 大文字 HEX 変換（`#` 無し）</summary>
        internal static string ColorToHex(Color c)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            return $"{r:X2}{g:X2}{b:X2}";
        }

        private static Color HexToColor(string hex)
        {
            int r = System.Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = System.Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = System.Convert.ToInt32(hex.Substring(4, 2), 16);
            return new Color(r / 255f, g / 255f, b / 255f, 1f);
        }

        private static bool IsValidHex(string s)
        {
            if (s.Length != 6) return false;
            foreach (char c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        // ================================================================
        //  リソース解放
        // ================================================================

        public void Dispose()
        {
            UnregisterOutsideClickHandler();

            if (_svPlane != null)
                _svPlane.generateVisualContent -= OnGenerateSvContent;
            if (_huePlane != null)
                _huePlane.generateVisualContent -= OnGenerateHueContent;

            if (_svPlane != null)
            {
                _svPlane.UnregisterCallback<PointerDownEvent>(OnSvPointerDown);
                _svPlane.UnregisterCallback<PointerMoveEvent>(OnSvPointerMove);
                _svPlane.UnregisterCallback<PointerUpEvent>(OnSvPointerUp);
            }
            if (_huePlane != null)
            {
                _huePlane.UnregisterCallback<PointerDownEvent>(OnHuePointerDown);
                _huePlane.UnregisterCallback<PointerMoveEvent>(OnHuePointerMove);
                _huePlane.UnregisterCallback<PointerUpEvent>(OnHuePointerUp);
            }
            if (_hexField != null)
            {
                _hexField.UnregisterCallback<KeyDownEvent>(OnHexKeyDown);
                _hexField.UnregisterCallback<FocusOutEvent>(OnHexFocusOut);
            }

            onChanged = null;
        }
    }
}
