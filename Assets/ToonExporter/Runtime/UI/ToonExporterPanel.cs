using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using ToonExporter.Core;
using ToonExporter.Runtime;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ToonExporter.UI
{
    /// <summary>
    /// トゥーンレイヤー書き出しツール UIToolkit パネル制御
    ///
    /// 責務：
    ///   - フロートパネルのガワ（ドラッグ・[H]トグル・動的maxHeight）
    ///   - SECTIONS 駆動ビルダー + ToonExporterState 往復 + ウィジェット配線
    ///   - プリセット / モーダル 4 種 / トーストレイヤー / PresetManager 通知購読
    ///   - StateChanged で ToonPreviewController にライブ反映を配信
    ///
    /// ライブプレビューの流れ：
    ///   - _dirty フラグ集約パターン: Panel.Update() で 1 フレーム 1 回に coalesce
    ///   - BuildStateFromUI: 副作用ゼロの純関数（PreviewController が都度呼ぶ）
    ///   - InitFromState: State から UI 値を書き戻し（末尾で StateChanged 明示発火）
    ///   - event Action StateChanged: ToonPreviewController が購読
    /// </summary>
    public class ToonExporterPanel : MonoBehaviour
    {
        [SerializeField] private UIDocument uiDocument;

        // 書き出しコントローラー。Inspector アサインが基本だが、
        // 未アサインなら EnsureExportController で同 GO 内 or AddComponent フォールバック
        [SerializeField] private ExportController exportController;

        // 画像入力コントローラー。Inspector アサインが基本、未アサイン時は
        // InitImageLoadBinding が同 GO をフォールバックで拾う
        [SerializeField] private ToonImageImporter imageImporter;

        // ================================================================
        //  UI 要素キャッシュ
        // ================================================================

        private VisualElement _root;
        private VisualElement _panel;
        private VisualElement _titlebar;
        private ScrollView _scroll;

        // パネル表示状態
        private bool _visible = true;

        // タイトルバードラッグ
        private bool _isDragging;
        private Vector2 _dragStartPointer;
        private Vector2 _dragStartPanelPos;

        private const float PanelMaxHeightCap = 760f;
        private const float PanelVerticalMargin = 80f;

        // ================================================================
        //  State / dirty / イベントフック
        // ================================================================

        private bool _dirty;

        /// <summary>State が更新されたことを通知する。ToonPreviewController が購読中</summary>
        public event Action StateChanged;

        // ================================================================
        //  ウィジェット辞書（キー = RowDescriptor.id）
        // ================================================================

        private readonly Dictionary<string, FoldableSection> _sections = new();
        private readonly Dictionary<string, LabeledSlider> _sliders = new();
        private readonly Dictionary<string, SegmentedControl> _segments = new();
        private readonly Dictionary<string, Vector2Pad> _pads = new();
        private readonly Dictionary<string, ToggleSwitch> _toggles = new();
        private readonly Dictionary<string, bool> _enableStates = new();

        /// <summary>色行: 色窓 + HEX ラベル + 現在色を束ねたキャッシュ</summary>
        private class ColorRow
        {
            public VisualElement swatch;
            public Label hexLabel;
            public Color color;
        }

        private readonly Dictionary<string, ColorRow> _colorRows = new();

        // カラーピッカー（単一インスタンスで全色窓が共有）
        private RuntimeColorPicker _colorPicker;
        private string _activeColorRowId;

        // ================================================================
        //  プリセット / モーダル / トースト
        // ================================================================

        // プリセットセクション（HeaderMode.None・常時開）
        private FoldableSection _presetSection;
        private VisualElement _presetListContainer;
        private readonly List<Button> _presetButtons = new();
        private string _activePresetName;

        // プリセット操作ボタン
        private Button _btnPresetAdd;
        private Button _btnPresetDelete;
        private Button _btnPresetReset;

        // モーダルオーバーレイ（動的に _root へ Add・単一）
        // ShowModal でセットし DismissModal で解除。購読/解除の対称性のためデリゲートをフィールド保持
        private VisualElement _modalOverlay;
        private Action _modalEnterAction;
        private EventCallback<KeyDownEvent> _onModalKeyDown;

        // プリセットロード中フラグ。「ロード直後に _activePresetName を誤クリアしない」ため
        private bool _loadingPreset;

        // PresetManager.OnNotification 購読ハンドラ（対称解除用）
        private Action<string, PresetManager.NotifyLevel> _onPresetNotification;

        // ================================================================
        //  書き出しボタン + ExportController 連携
        // ================================================================

        private Button _btnExport;

        // ExportController.ExportStateChanged 購読ハンドラ（対称解除用）
        private Action _onExportStateChanged;

        // ================================================================
        //  画像ロードボタン + ImageImporter 状態購読
        // ================================================================

        private Button _btnLoadMain;
        private Button _btnLoadNormal;

        // OnIllustrationLoaded / OnNormalLoaded / OnShadowTextureLoaded 購読ハンドラ（対称解除用）
        private Action _onIllustLoaded;
        private Action _onNormalLoaded;
        private Action _onShadowTexLoaded;

        // Toggle 行のインライン読み込みボタン（ラベル更新用にキャッシュ）
        private readonly Dictionary<string, Button> _rowButtons = new();

        // ================================================================
        //  SECTIONS 記述子定義（Toon2DLayered.shader プロパティから起こす）
        // ================================================================

        private enum RowType { Toggle, Slider, Color, Point, Segment }

        private struct RowDescriptor
        {
            public string id;
            public string label;
            public RowType type;
            public float min, max, step;
            public string[] segmentLabels;
            // mainLightDir がリムの UV シフト方向も兼務する意味論のねじれを tooltip で逃がす。空文字なら未設定
            public string tooltip;
            // Toggle 行のラベルとトグルの間にインライン読み込みボタンを差すか（影1「影の読み込み」用）
            public bool hasLoadButton;
        }

        private struct SectionDescriptor
        {
            public string id;
            public string title;
            public string subtitle;
            public FoldableSection.HeaderMode headerMode;
            public RowDescriptor[] rows;
        }

        // 4 スロット（影1/影2/リム/サブ）
        // targetCount は BuildStateFromUI で 4 固定代入。
        // sub.intensity 行は撤去し FixedSubIntensity=0.1f を固定代入。
        private static readonly SectionDescriptor[] SECTIONS = new SectionDescriptor[]
        {
            // ━━ 影1 ━━
            // shadow1.mainLightDir は state.mainLightDirection にマップ（=_Light）。
            // _Light はリムの UV シフト方向も兼務するため tooltip で明示。
            new SectionDescriptor
            {
                id = "shadow1", title = "影1", subtitle = "Shadow 1",
                headerMode = FoldableSection.HeaderMode.Toggle,
                rows = new RowDescriptor[]
                {
                    new RowDescriptor { id = "shadow1.threshold",    label = "ライト奥行き", type = RowType.Slider, min = 0f, max = 1.2f, step = 0.01f },
                    new RowDescriptor
                    {
                        id = "shadow1.mainLightDir", label = "ライト方向",
                        type = RowType.Point, min = 0f, max = 1f,
                        tooltip = "リムの向きもこの方向に追従します",
                    },
                    new RowDescriptor { id = "shadow1.softness",       label = "エッジぼかし", type = RowType.Slider, min = 0f, max = 1f,   step = 0.01f },
                    new RowDescriptor { id = "shadow1.color",          label = "影色",         type = RowType.Color },
                    new RowDescriptor { id = "shadow1.useColorTexture", label = "影の読み込み", type = RowType.Toggle, hasLoadButton = true },
                }
            },
            // ━━ 影2 ━━
            new SectionDescriptor
            {
                id = "shadow2", title = "影2", subtitle = "Shadow 2",
                headerMode = FoldableSection.HeaderMode.Toggle,
                rows = new RowDescriptor[]
                {
                    new RowDescriptor { id = "shadow2.threshold", label = "ライト奥行き", type = RowType.Slider, min = 0f, max = 1.2f, step = 0.01f },
                    new RowDescriptor { id = "shadow2.lightDir",  label = "ライト方向",   type = RowType.Point,  min = 0f, max = 1f },
                    new RowDescriptor { id = "shadow2.softness",  label = "エッジぼかし", type = RowType.Slider, min = 0f, max = 1f,   step = 0.01f },
                    new RowDescriptor { id = "shadow2.color",     label = "影色",         type = RowType.Color },
                }
            },
            // ━━ リム ━━
            // _useLimlight, _limLightWidth(0..1), _rimColor
            new SectionDescriptor
            {
                id = "rim", title = "リム", subtitle = "Rim Light",
                headerMode = FoldableSection.HeaderMode.Toggle,
                rows = new RowDescriptor[]
                {
                    new RowDescriptor { id = "rim.width", label = "幅",     type = RowType.Slider, min = 0f, max = 1f, step = 0.01f },
                    new RowDescriptor { id = "rim.color", label = "リム色", type = RowType.Color },
                }
            },
            // ━━ サブライト ━━
            // intensity 行は撤去済み（FixedSubIntensity 固定・BuildStateFromUI で代入）
            new SectionDescriptor
            {
                id = "sub", title = "サブライト", subtitle = "Sub Light",
                headerMode = FoldableSection.HeaderMode.Toggle,
                rows = new RowDescriptor[]
                {
                    new RowDescriptor { id = "sub.threshold", label = "ライト奥行き", type = RowType.Slider, min = 0f, max = 1.2f, step = 0.01f },
                    new RowDescriptor { id = "sub.softness",  label = "ぼかし",       type = RowType.Slider, min = 0f, max = 1f,   step = 0.01f },
                    new RowDescriptor { id = "sub.color",     label = "サブ色",       type = RowType.Color },
                    new RowDescriptor { id = "sub.lightDir",  label = "ライト方向",   type = RowType.Point,  min = 0f, max = 1f },
                }
            },
        };

        // ================================================================
        //  Awake
        // ================================================================

        private void Awake()
        {
            if (uiDocument == null)
            {
                Debug.LogError("[ToonExporterPanel] uiDocument が未アサイン");
                return;
            }

            _root = uiDocument.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[ToonExporterPanel] rootVisualElement が取得できない");
                return;
            }

            _panel = _root.Q("toon-panel");
            _titlebar = _root.Q("toon-titlebar");
            _scroll = _root.Q<ScrollView>("toon-scroll");

            // --- タイトルバードラッグ移動 ---
            if (_titlebar != null)
            {
                _titlebar.RegisterCallback<PointerDownEvent>(OnTitlebarPointerDown);
                _titlebar.RegisterCallback<PointerMoveEvent>(OnTitlebarPointerMove);
                _titlebar.RegisterCallback<PointerUpEvent>(OnTitlebarPointerUp);
            }

            // --- カラーピッカー → セクション構築 → State 初期化 ---
            InitColorPicker();
            BuildAllSections();
            InitFromState(new ToonExporterState());

            // --- トースト → プリセット通知購読 → プリセットセクション ---
            // 順序が重要:
            //   1) ToastManager を確保して描画レイヤーを注入（他の Init より前）
            //   2) PresetManager の通知を Toast に橋渡し（購読）
            //   3) PresetManager.Initialize（ディレクトリ作成・デフォルトコピー）
            //   4) プリセットセクション構築（一覧取得は Initialize 後）
            EnsureToastManager();
            InitToastLayer();
            SubscribePresetNotifications();
            PresetManager.Initialize();
            BuildPresetSection();

            // --- 書き出しボタン配線 + ExportController 状態購読 ---
            InitExportBinding();

            // --- 画像ロードボタン配線 + ImageImporter 通知購読 ---
            // InitExportBinding の後に置いて、ImageImporter → Export ボタン活性化の順序整合を取る
            InitImageLoadBinding();

            // --- `_useColorTexture` トグルの「未ロード時ガード」---
            // BuildAllSections で辞書へ詰めた後・InitImageLoadBinding で imageImporter を
            // 解決した後にしか判定できないのでここで呼ぶ（未ロード ON で
            // _shadowTexture のデフォルト "black" が影ソース化する真っ黒事故を防ぐ）。
            InitShadowTextureToggleGuard();

            // --- ルートリサイズで maxHeight を動的制御 ---
            _root.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        // ================================================================
        //  書き出しボタン配線
        // ================================================================

        private void InitExportBinding()
        {
            _btnExport = _root.Q<Button>("toon-btn-export");
            if (_btnExport == null)
            {
                Debug.LogWarning("[ToonExporterPanel] toon-btn-export が見つからない（UXML 確認）");
                return;
            }

            // ExportController 未アサインなら同 GO を探して埋める（ユーザーの手配線を減らす）。
            // 見つからなければボタン disabled のまま。
            if (exportController == null)
            {
                exportController = GetComponent<ExportController>();
            }

            if (exportController == null)
            {
                _btnExport.SetEnabled(false);
                _btnExport.tooltip = "ExportController が未アサイン。Inspector で設定して";
                Debug.LogWarning("[ToonExporterPanel] ExportController 未アサイン。書き出しボタンは無効化");
                return;
            }

            _btnExport.clicked += OnExportClicked;

            // 書き出し状態変更を購読して btn.enabled を反転
            _onExportStateChanged = UpdateExportButtonState;
            exportController.ExportStateChanged += _onExportStateChanged;

            UpdateExportButtonState();
        }

        private void OnExportClicked()
        {
            if (exportController == null) return;
            // Panel から現時点の State を組んで渡す。RT 配列取得は ExportController 内部で完結。
            exportController.StartExport(BuildStateFromUI());
        }

        private void UpdateExportButtonState()
        {
            if (_btnExport == null || exportController == null) return;

            // イラスト未ロード時は真っ白 PNG が出るだけで意味ないので押させない。
            // ノーマル未ロードは OK（フラット法線でフォールバック成立）。
            bool hasIllust = imageImporter == null || imageImporter.HasIllustration;
            bool canExport = !exportController.IsExporting && hasIllust;
            _btnExport.SetEnabled(canExport);

            _btnExport.tooltip = (imageImporter != null && !imageImporter.HasIllustration)
                ? "「イラストを読み込み」からイラストを選ぶと書き出せます"
                : string.Empty;
        }

        // ================================================================
        //  画像ロードボタン配線
        // ================================================================

        private void InitImageLoadBinding()
        {
            _btnLoadMain   = _root.Q<Button>("toon-btn-load-main");
            _btnLoadNormal = _root.Q<Button>("toon-btn-load-normal");
            if (_btnLoadMain == null || _btnLoadNormal == null)
            {
                Debug.LogWarning("[ToonExporterPanel] toon-btn-load-{main,normal} が見つからない（UXML 確認）");
                return;
            }

            if (imageImporter == null)
            {
                imageImporter = GetComponent<ToonImageImporter>();
            }

            if (imageImporter == null)
            {
                _btnLoadMain.SetEnabled(false);
                _btnLoadNormal.SetEnabled(false);
                _btnLoadMain.tooltip   = "ToonImageImporter が未アサイン。Inspector で設定して";
                _btnLoadNormal.tooltip = "ToonImageImporter が未アサイン。Inspector で設定して";
                Debug.LogWarning("[ToonExporterPanel] ToonImageImporter 未アサイン。画像ロードボタンは無効化");
                return;
            }

            _btnLoadMain.clicked   += OnLoadMainClicked;
            _btnLoadNormal.clicked += OnLoadNormalClicked;

            // ロード完了時にボタンラベル更新（"イラスト: toon.png" 形式）。
            // イラスト側は書き出しボタン再評価も兼ねる（HasIllustration 反映）
            _onIllustLoaded = () =>
            {
                UpdateLoadMainLabel();
                UpdateExportButtonState();
            };
            _onNormalLoaded = UpdateLoadNormalLabel;
            imageImporter.OnIllustrationLoaded += _onIllustLoaded;
            imageImporter.OnNormalLoaded       += _onNormalLoaded;

            // 影テクスチャロード完了時の自動 ON 配線
            _onShadowTexLoaded = () =>
            {
                // ロードでトグルを自動 ON（SetValueWithoutNotify なので _dirty は立たない）
                SetToggle("shadow1.useColorTexture", true);
                // 未ロードガード解除
                SetUseColorTextureToggleEnabled(true);
                UpdateShadowTexButtonLabel();
                // _dirty を立てないと次のユーザー操作まで Material に届かない（Update→StateChanged 経路）
                _dirty = true;
            };
            imageImporter.OnShadowTextureLoaded += _onShadowTexLoaded;

            UpdateLoadMainLabel();
            UpdateLoadNormalLabel();
            UpdateShadowTexButtonLabel();

            // InitExportBinding は Awake の早い段階で走って UpdateExportButtonState を叩くが、
            // その時点で imageImporter はまだ null なので、HasIllustration ガードを効かせるためここでも叩く
            UpdateExportButtonState();
        }

        private void OnLoadMainClicked()
        {
            imageImporter?.OpenIllustrationDialog();
        }

        private void OnLoadNormalClicked()
        {
            imageImporter?.OpenNormalDialog();
        }

        private void UpdateLoadMainLabel()
        {
            if (_btnLoadMain == null || imageImporter == null) return;
            string name = imageImporter.LoadedIllustFileName;
            _btnLoadMain.text = string.IsNullOrEmpty(name)
                ? "イラストを読み込み"
                : $"イラスト: {name}";
        }

        private void UpdateLoadNormalLabel()
        {
            if (_btnLoadNormal == null || imageImporter == null) return;
            string name = imageImporter.LoadedNormalFileName;
            _btnLoadNormal.text = string.IsNullOrEmpty(name)
                ? "ノーマルを読み込み"
                : $"ノーマル: {name}";
        }

        // ================================================================
        //  `_useColorTexture` トグルの「未ロード時ガード」
        //
        //  影テクスチャ未ロード時のみトグルを操作不可にする（ロードで自動解除）。
        //  _shadowTexture のデフォルトは "black" なので、未ロードで useColorTexture を
        //  ON にできると影ソースが黒一色になる真っ黒事故になる。
        // ================================================================

        private void InitShadowTextureToggleGuard()
        {
            bool loaded = imageImporter != null && imageImporter.HasShadowTexture;
            SetUseColorTextureToggleEnabled(loaded);
        }

        /// <summary>
        /// 影1「影の読み込み」トグルの操作可否を切り替える。
        /// enabled=false（未ロード）: 構造ブロック＋グレーアウト＋案内 tooltip。
        /// enabled=true（ロード済み）: 通常操作に戻す。
        /// </summary>
        private void SetUseColorTextureToggleEnabled(bool enabled)
        {
            if (!_toggles.TryGetValue("shadow1.useColorTexture", out var toggle)) return;

            // enabledInHierarchy 経由で ToggleSwitch._track の PointerDownEvent が届かなくなる
            toggle.SetEnabled(enabled);

            // .toon-toggle には :disabled ルール無いので opacity グレーアウトは明示クラスで。
            // 行ごとじゃなくトグル本体に付ける＝隣のインライン読み込みボタンは明るいまま残して
            // 「ここから読み込め」の導線を潰さない
            toggle.EnableInClassList("toon-row--disabled", !enabled);
            toggle.tooltip = enabled ? string.Empty : "先に影テクスチャを読み込んでください";
        }

        /// <summary>
        /// 影1「影の読み込み」インラインボタンのラベルを更新。
        /// 未ロードは「画像を選択」、ロード済みは「読み込み済み: xxx.png」。
        /// </summary>
        private void UpdateShadowTexButtonLabel()
        {
            if (imageImporter == null) return;
            if (!_rowButtons.TryGetValue("shadow1.useColorTexture", out var btn) || btn == null) return;

            string name = imageImporter.LoadedShadowTexFileName;
            btn.text = string.IsNullOrEmpty(name)
                ? "画像を選択"
                : $"読み込み済み: {name}";
        }

        // ================================================================
        //  ToastManager 準備（シーンに未配置なら AddComponent で自動セットアップ）
        // ================================================================

        private void EnsureToastManager()
        {
            if (ToastManager.Instance != null) return;

            // 同 GameObject に付ける。ToastManager 自身が Awake でシングルトン登録する。
            gameObject.AddComponent<ToastManager>();
        }

        // ================================================================
        //  トーストレイヤー初期化
        //  ToastManager の描画先を _root 配下に作って注入する。
        //  pickingMode=Ignore で背面操作（モーダル・パネル）を一切妨げない。
        // ================================================================

        private void InitToastLayer()
        {
            var toastLayer = new VisualElement();
            toastLayer.AddToClassList("toon-toast__layer");
            toastLayer.pickingMode = PickingMode.Ignore;

            // _root の直下に追加（パネルやカラーピッカーと同階層）
            _root.Add(toastLayer);

            // ToastManager にレイヤーを渡す（OnDestroy 側は
            // ToastManager.Instance.DetachLayer で外すので参照キャッシュ不要）
            if (ToastManager.Instance != null)
                ToastManager.Instance.AttachLayer(toastLayer);
            else
                Debug.LogWarning("[ToonExporterPanel] ToastManager.Instance が null。トーストレイヤー注入スキップ");
        }

        // ================================================================
        //  PresetManager 通知 → Toast 橋渡し
        //  Core は UI 依存禁止で static event で通知するだけなので、UI 側でトーストに変換
        // ================================================================

        private void SubscribePresetNotifications()
        {
            _onPresetNotification = (message, level) =>
            {
                ToastManager.ToastLevel toastLevel = level switch
                {
                    PresetManager.NotifyLevel.Success => ToastManager.ToastLevel.Success,
                    PresetManager.NotifyLevel.Warning => ToastManager.ToastLevel.Warning,
                    PresetManager.NotifyLevel.Error   => ToastManager.ToastLevel.Error,
                    _                                 => ToastManager.ToastLevel.Info,
                };
                ToastManager.ShowOrLog(message, toastLevel);
            };
            PresetManager.OnNotification += _onPresetNotification;
        }

        // ================================================================
        //  Update — [H] トグル + _dirty 駆動
        // ================================================================

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            bool hPressed = Keyboard.current != null
                         && Keyboard.current.hKey.wasPressedThisFrame;
#else
            bool hPressed = Input.GetKeyDown(KeyCode.H);
#endif

            if (hPressed)
            {
                // テキスト入力中は H トグルを無視
                var focused = _root?.focusController?.focusedElement;
                if (focused is TextField || focused is FloatField)
                {
                    // テキスト入力中 → トグルスキップ
                }
                else
                {
                    _visible = !_visible;
                    if (_panel != null)
                    {
                        _panel.style.display = _visible
                            ? DisplayStyle.Flex
                            : DisplayStyle.None;
                    }
                }
            }

            // _dirty → StateChanged 発火（ToonPreviewController が購読中のライブ反映発火点）
            if (_dirty)
            {
                _dirty = false;

                // ユーザー由来の値変更ならアクティブプリセット表示を解除。
                // _loadingPreset サンドイッチ中（=Load/Reset 経由の InitFromState）は触らない。
                // InitFromState で SetValueWithoutNotify のみ使うので現実的にはここへ到達しないはずだが保険
                if (!_loadingPreset && !string.IsNullOrEmpty(_activePresetName))
                {
                    _activePresetName = null;
                    UpdatePresetActiveVisual();
                }

                StateChanged?.Invoke();
            }
        }

        // ================================================================
        //  SECTIONS 駆動ビルダー
        // ================================================================

        private void BuildAllSections()
        {
            if (_scroll == null) return;

            foreach (var sec in SECTIONS)
            {
                // ターゲット設定（None）は初期 open、スロット（Toggle）は初期 closed
                bool initialOpen = sec.headerMode != FoldableSection.HeaderMode.Toggle;
                var section = new FoldableSection(sec.title, sec.subtitle, sec.headerMode, initialOpen);
                _scroll.Add(section);
                _sections[sec.id] = section;

                // Toggle モードのセクションは Enable 状態を追跡
                if (sec.headerMode == FoldableSection.HeaderMode.Toggle)
                {
                    _enableStates[sec.id] = false;
                    string sectionId = sec.id;
                    section.onToggleChanged += v => OnSectionToggleChanged(sectionId, v);

                    // 影1トグルは "常時ON表示 + クリック不可" に固定。
                    // Shader に `_UseShadow1` 相当のプロパティが無いので、
                    // 影1だけ ON/OFF できても Apply 先が無い＝見た目と State が矛盾する。
                    // 表示側でグレーアウトして触らせない。State レベルは BuildStateFromUI で強制 true
                    if (sec.id == "shadow1")
                    {
                        _enableStates["shadow1"] = true;
                        section.SetToggleWithoutNotify(true);
                        section.SetBodyEnabled(true);
                        section.Toggle?.SetEnabled(false);
                    }
                }

                foreach (var row in sec.rows)
                {
                    BuildRow(section, row);
                }
            }
        }

        /// <summary>
        /// 記述子 1 行分のウィジェットを生成してセクション body に追加
        /// </summary>
        private void BuildRow(FoldableSection section, RowDescriptor row)
        {
            switch (row.type)
            {
                case RowType.Slider:
                {
                    var slider = new LabeledSlider(row.label, row.min, row.max, row.step);
                    section.AddToBody(slider);
                    _sliders[row.id] = slider;
                    slider.onChanged += _ => { _dirty = true; };
                    break;
                }

                case RowType.Segment:
                {
                    var seg = new SegmentedControl(row.label, row.segmentLabels);
                    section.AddToBody(seg);
                    _segments[row.id] = seg;
                    seg.onChanged += _ => { _dirty = true; };
                    break;
                }

                case RowType.Point:
                {
                    var pad = new Vector2Pad(row.label);
                    if (!string.IsNullOrEmpty(row.tooltip)) pad.tooltip = row.tooltip;
                    section.AddToBody(pad);
                    _pads[row.id] = pad;
                    pad.onChanged += _ => { _dirty = true; };
                    break;
                }

                case RowType.Color:
                {
                    BuildColorRow(section, row);
                    break;
                }

                case RowType.Toggle:
                {
                    var toggleRow = new VisualElement();
                    toggleRow.AddToClassList("toon-row");

                    var lbl = new Label(row.label);
                    lbl.AddToClassList("toon-row__label");
                    toggleRow.Add(lbl);

                    // ラベルとトグルの間にインライン読み込みボタンを差し込む（影1「影の読み込み」）。
                    // imageImporter はクリック時に評価される（BuildRow 時点では null でも
                    // InitImageLoadBinding 後に解決済みなので ?. で安全）
                    if (row.hasLoadButton)
                    {
                        var loadBtn = new Button { text = "画像を選択" };
                        loadBtn.AddToClassList("toon-row__inline-btn");
                        loadBtn.clicked += () => imageImporter?.OpenShadowTextureDialog();
                        toggleRow.Add(loadBtn);
                        _rowButtons[row.id] = loadBtn;
                    }

                    var toggle = new ToggleSwitch();
                    toggleRow.Add(toggle);

                    section.AddToBody(toggleRow);
                    _toggles[row.id] = toggle;
                    toggle.onChanged += _ => { _dirty = true; };
                    break;
                }
            }
        }

        /// <summary>
        /// 色行を構築: ラベル + 色窓(swatch) + HEX 表示。
        /// 色窓クリックで共有カラーピッカーを開く。
        /// </summary>
        private void BuildColorRow(FoldableSection section, RowDescriptor row)
        {
            var colorRow = new VisualElement();
            colorRow.AddToClassList("toon-row");
            colorRow.AddToClassList("toon-row--color");

            var label = new Label(row.label);
            label.AddToClassList("toon-row__label");
            colorRow.Add(label);

            var swatch = new VisualElement();
            swatch.AddToClassList("toon-swatch");
            swatch.style.backgroundColor = Color.white;
            colorRow.Add(swatch);

            var hex = new Label($"#{RuntimeColorPicker.ColorToHex(Color.white)}");
            hex.AddToClassList("toon-color__hex");
            colorRow.Add(hex);

            section.AddToBody(colorRow);

            var cr = new ColorRow { swatch = swatch, hexLabel = hex, color = Color.white };
            _colorRows[row.id] = cr;

            string rowId = row.id;
            swatch.RegisterCallback<PointerDownEvent>(evt => OnSwatchClick(evt, rowId));
        }

        // ================================================================
        //  カラーピッカー初期化
        // ================================================================

        private void InitColorPicker()
        {
            _colorPicker = new RuntimeColorPicker();

            var toonApp = _root.Q("toon-app");
            if (toonApp != null)
                toonApp.Add(_colorPicker);
            else
                _root.Add(_colorPicker);

            _colorPicker.onChanged += OnColorPickerChanged;
        }

        // ================================================================
        //  プリセットセクション（SECTIONS 記述子には載せない・wrap レイアウトで別構造）
        //  PresetManager の API を呼ぶだけ。State/UI 反映は本ファイル内で完結。
        // ================================================================

        /// <summary>
        /// プリセットセクションを構築。
        /// HeaderMode.None でトグルなし、ScrollView の先頭に挿入。
        /// ボタン一覧は PresetManager.GetPresetNames() から動的生成。
        /// </summary>
        private void BuildPresetSection()
        {
            _presetSection = new FoldableSection("プリセット", "Preset", FoldableSection.HeaderMode.None, initialOpen: true);

            // プリセットはパラメータ調整の前に置く。
            // _scroll の先頭（settings セクションの前）に Insert
            if (_scroll != null && _scroll.childCount > 0)
                _scroll.Insert(0, _presetSection);
            else
                _scroll?.Add(_presetSection);

            // wrap 配置コンテナ
            _presetListContainer = new VisualElement();
            _presetListContainer.AddToClassList("toon-preset__list");
            _presetSection.AddToBody(_presetListContainer);

            // ボタン群を生成
            RefreshPresetButtons();

            // --- 操作ボタン行（＋登録 / −削除 / ↻リセット）---
            var actionRow = new VisualElement();
            actionRow.AddToClassList("toon-preset__action-row");

            _btnPresetAdd = new Button { text = "＋ 登録" };
            _btnPresetAdd.AddToClassList("toon-preset__action-btn");
            _btnPresetAdd.clicked += OnPresetAddClicked;

            _btnPresetDelete = new Button { text = "− 削除" };
            _btnPresetDelete.AddToClassList("toon-preset__action-btn");
            _btnPresetDelete.clicked += OnPresetDeleteClicked;

            _btnPresetReset = new Button { text = "↻ リセット" };
            _btnPresetReset.AddToClassList("toon-preset__action-btn");
            _btnPresetReset.clicked += OnPresetResetClicked;

            actionRow.Add(_btnPresetAdd);
            actionRow.Add(_btnPresetDelete);
            actionRow.Add(_btnPresetReset);
            _presetSection.AddToBody(actionRow);

            // 初期状態：アクティブプリセットがなければ削除ボタン disabled
            UpdateDeleteButtonState();
        }

        /// <summary>
        /// プリセットボタンを全再生成。
        /// Save/Delete 後にも呼べるようメソッド化。
        /// </summary>
        private void RefreshPresetButtons()
        {
            if (_presetListContainer == null) return;

            _presetListContainer.Clear();
            _presetButtons.Clear();

            List<string> names = PresetManager.GetPresetNames();
            foreach (string name in names)
            {
                var btn = new Button { text = name };
                btn.AddToClassList("toon-preset__btn");

                // アクティブ状態の復元（前回ロードしたプリセットをハイライト）
                if (name == _activePresetName)
                    btn.AddToClassList("toon-preset__btn--active");

                // クリックでロード
                string presetName = name; // ラムダキャプチャ用
                btn.clicked += () => OnPresetClicked(presetName);

                _presetListContainer.Add(btn);
                _presetButtons.Add(btn);
            }
        }

        /// <summary>
        /// プリセットボタンクリック → ロード → UI 全反映。
        ///
        /// InitFromState は SetValueWithoutNotify を使うので _dirty は立たない
        /// （＝onChanged 経由の自然発火はしない・意図通り）。
        /// ライブ反映に届かせるため末尾で明示的に StateChanged を発火する。
        /// </summary>
        private void OnPresetClicked(string presetName)
        {
            ToonExporterState state = PresetManager.Load(presetName);
            if (state == null) return;

            _activePresetName = presetName;
            UpdatePresetActiveVisual();
            UpdateDeleteButtonState();

            // 「誤クリア防止」フラグ。将来 _dirty→_activePresetName クリア経路が増えた時の拠点
            _loadingPreset = true;

            InitFromState(state);

            // プリセット読込末尾で StateChanged 明示発火。
            // InitFromState 内は SetValueWithoutNotify なので _dirty ルートでは発火しない
            StateChanged?.Invoke();

            _loadingPreset = false;
        }

        /// <summary>
        /// プリセットボタンのアクティブ表示を更新。
        /// 全ボタンから active クラスを外して、現在のアクティブだけ付与。
        /// </summary>
        private void UpdatePresetActiveVisual()
        {
            foreach (var btn in _presetButtons)
            {
                bool isActive = btn.text == _activePresetName;
                btn.EnableInClassList("toon-preset__btn--active", isActive);
            }
        }

        /// <summary>削除ボタンの有効/無効をアクティブプリセット有無で切り替える</summary>
        private void UpdateDeleteButtonState()
        {
            if (_btnPresetDelete == null) return;
            _btnPresetDelete.SetEnabled(!string.IsNullOrEmpty(_activePresetName));
        }

        // ================================================================
        //  プリセット登録 / 削除 / リセット — モーダル制御
        //  _root の最前面にオーバーレイを追加して表示制御する方式。
        //  PresetManager の API は呼ぶだけ、変更はしない。
        // ================================================================

        /// <summary>「＋登録」ボタン押下 → 登録モーダルを表示</summary>
        private void OnPresetAddClicked()
        {
            ShowSaveModal();
        }

        /// <summary>「−削除」ボタン押下 → 削除確認モーダルを表示</summary>
        private void OnPresetDeleteClicked()
        {
            if (string.IsNullOrEmpty(_activePresetName)) return;
            ShowDeleteModal(_activePresetName);
        }

        /// <summary>「↻リセット」ボタン押下 → 初期化確認モーダルを表示</summary>
        private void OnPresetResetClicked()
        {
            ShowResetConfirmModal();
        }

        // ------------------------------------------------------------------
        //  登録モーダル
        // ------------------------------------------------------------------

        /// <summary>
        /// プリセット名入力→保存のモーダルを表示。
        /// 既存名なら上書き確認を挟む。
        /// </summary>
        private void ShowSaveModal()
        {
            // 入力用 TextField
            var nameField = new TextField();
            nameField.AddToClassList("toon-modal__text-field");
            // プレースホルダー的に空文字で初期化
            // （UI Toolkit の placeholder はバージョン依存なので省略）
            nameField.value = "";

            // ボタン行
            var btnRow = new VisualElement();
            btnRow.AddToClassList("toon-modal__btn-row");

            var btnCancel = new Button { text = "キャンセル" };
            btnCancel.AddToClassList("toon-modal__btn");
            btnCancel.AddToClassList("toon-modal__btn--cancel");

            var btnSave = new Button { text = "登録" };
            btnSave.AddToClassList("toon-modal__btn");
            btnSave.AddToClassList("toon-modal__btn--primary");

            btnRow.Add(btnCancel);
            btnRow.Add(btnSave);

            // パネル構築
            var panel = BuildModalPanel("プリセット登録", nameField, btnRow);

            // 保存ロジック（ボタンクリックと Enter キーの両方から呼ぶ）
            void TrySave()
            {
                string inputName = nameField.value?.Trim();

                // バリデーション：空文字・空白のみ
                if (string.IsNullOrWhiteSpace(inputName))
                {
                    ToastManager.ShowOrLog("名前を入力してね", ToastManager.ToastLevel.Warning);
                    return;
                }

                // 既存名チェック → 上書き確認
                if (PresetManager.Exists(inputName))
                {
                    DismissModal();
                    ShowOverwriteConfirmModal(inputName);
                    return;
                }

                // 新規保存
                ExecutePresetSave(inputName);
            }

            btnCancel.clicked += () => DismissModal();
            btnSave.clicked += TrySave;

            ShowModal(panel, onEnterAction: TrySave);

            // --- TextField 上の Enter/ESC 初回不発バグ対策 ---
            // UI Toolkit のシングルライン TextField は内部で Enter=値確定 /
            // ESC=入力キャンセルをバブル前に処理して KeyDownEvent を消費するため、
            // 通常登録（バブル段階）では届かない。TrickleDown でキャプチャフェーズに
            // 登録して、TextField 内部処理より先取りする。
            // overlay 側ハンドラとの二重発火は TrySave→DismissModal→overlay 除去で
            // 2 回目空振り。
            nameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    TrySave();
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    // ユーザー希望：ESC 1 回で文字消しではなくモーダル閉じ。
                    // TextField 内部の「ESC=文字消し」を先取り抑止する。
                    DismissModal();
                    evt.StopPropagation();
                }
            }, TrickleDown.TrickleDown);

            // 表示後にフォーカスを当てる。
            // schedule.Execute だとレイアウト確定前に走るバージョンがあり、
            // Enter/ESC 初回が「フォーカス確定」で消費されて二回押し必須になる
            // 症状が出た。GeometryChangedEvent でレイアウト確定後に発火させ、
            // さらに内部の "unity-text-input" 要素に直接フォーカス当てて確実化する。
            void OnNameFieldGeometryReady(GeometryChangedEvent _)
            {
                nameField.UnregisterCallback<GeometryChangedEvent>(OnNameFieldGeometryReady);
                var textInput = nameField.Q("unity-text-input");
                if (textInput != null) textInput.Focus();
                else nameField.Focus();
            }
            nameField.RegisterCallback<GeometryChangedEvent>(OnNameFieldGeometryReady);
        }

        /// <summary>上書き確認モーダル</summary>
        private void ShowOverwriteConfirmModal(string presetName)
        {
            var message = new Label($"「{presetName}」は既に存在します。上書きしますか？");
            message.AddToClassList("toon-modal__message");

            var btnRow = new VisualElement();
            btnRow.AddToClassList("toon-modal__btn-row");

            var btnCancel = new Button { text = "キャンセル" };
            btnCancel.AddToClassList("toon-modal__btn");
            btnCancel.AddToClassList("toon-modal__btn--cancel");

            var btnOverwrite = new Button { text = "上書き" };
            btnOverwrite.AddToClassList("toon-modal__btn");
            btnOverwrite.AddToClassList("toon-modal__btn--primary");

            btnRow.Add(btnCancel);
            btnRow.Add(btnOverwrite);

            var panel = BuildModalPanel("上書き確認", message, btnRow);

            btnCancel.clicked += () => DismissModal();
            btnOverwrite.clicked += () => ExecutePresetSave(presetName);

            ShowModal(panel, onEnterAction: () => ExecutePresetSave(presetName));
        }

        /// <summary>
        /// 実際の保存処理。保存成功 → 一覧リフレッシュ＋アクティブ更新＋モーダル閉じ。
        /// PresetManager.Save が内部でバリデーション＋通知するので、ここでは結果だけ見る。
        /// </summary>
        private void ExecutePresetSave(string presetName)
        {
            ToonExporterState currentState = BuildStateFromUI();
            bool success = PresetManager.Save(presetName, currentState);

            if (!success) return;

            _activePresetName = presetName;
            RefreshPresetButtons();
            UpdateDeleteButtonState();
            DismissModal();
        }

        // ------------------------------------------------------------------
        //  削除モーダル
        // ------------------------------------------------------------------

        /// <summary>削除確認モーダルを表示</summary>
        private void ShowDeleteModal(string presetName)
        {
            var message = new Label($"「{presetName}」を削除しますか？");
            message.AddToClassList("toon-modal__message");

            var btnRow = new VisualElement();
            btnRow.AddToClassList("toon-modal__btn-row");

            var btnCancel = new Button { text = "キャンセル" };
            btnCancel.AddToClassList("toon-modal__btn");
            btnCancel.AddToClassList("toon-modal__btn--cancel");

            var btnDelete = new Button { text = "削除" };
            btnDelete.AddToClassList("toon-modal__btn");
            btnDelete.AddToClassList("toon-modal__btn--danger");

            btnRow.Add(btnCancel);
            btnRow.Add(btnDelete);

            var panel = BuildModalPanel("プリセット削除", message, btnRow);

            // 削除実行ロジック（ボタンクリックと Enter キーの両方から呼ぶ）
            void ExecuteDelete()
            {
                PresetManager.Delete(presetName);

                // アクティブクリア＋一覧リフレッシュ
                _activePresetName = null;
                RefreshPresetButtons();
                UpdateDeleteButtonState();
                DismissModal();
            }

            btnCancel.clicked += () => DismissModal();
            btnDelete.clicked += ExecuteDelete;

            ShowModal(panel, onEnterAction: ExecuteDelete);
        }

        // ------------------------------------------------------------------
        //  パラメータ初期化（リセット）モーダル
        // ------------------------------------------------------------------

        /// <summary>
        /// パラメータ初期化の確認モーダル。
        /// 全パラメータを初期値に戻す強制操作なので、削除モーダルと同じ流儀で
        /// 確認モーダル＋クリック必須のセーフティネットを維持する。
        /// </summary>
        private void ShowResetConfirmModal()
        {
            var message = new Label("全パラメータを初期値に戻します。よろしいですか？");
            message.AddToClassList("toon-modal__message");

            var btnRow = new VisualElement();
            btnRow.AddToClassList("toon-modal__btn-row");

            var btnCancel = new Button { text = "キャンセル" };
            btnCancel.AddToClassList("toon-modal__btn");
            btnCancel.AddToClassList("toon-modal__btn--cancel");

            var btnReset = new Button { text = "リセット" };
            btnReset.AddToClassList("toon-modal__btn");
            btnReset.AddToClassList("toon-modal__btn--primary");

            btnRow.Add(btnCancel);
            btnRow.Add(btnReset);

            var panel = BuildModalPanel("パラメータ初期化", message, btnRow);

            // リセット実行ロジック
            void ExecuteReset()
            {
                _loadingPreset = true;

                // 新規 State でリセット。ライブ反映に届かせるため StateChanged 明示発火
                InitFromState(new ToonExporterState());
                StateChanged?.Invoke();

                _loadingPreset = false;

                // アクティブプリセットをクリア（初期値なので特定プリセットに対応しない）
                _activePresetName = null;
                UpdatePresetActiveVisual();
                UpdateDeleteButtonState();

                DismissModal();
            }

            btnCancel.clicked += () => DismissModal();
            btnReset.clicked += ExecuteReset;

            // リセットモーダルは削除モーダルと完全整合の挙動。
            // 初期状態はフォーカス未設定で Enter 効かない＝誤操作セーフティ。
            // ユーザーが一度クリック後は Enter で即実行可能（学習コスト低）。
            // ESC でキャンセルは ShowModal 側で常に効く。
            ShowModal(panel, onEnterAction: ExecuteReset);
        }

        // ------------------------------------------------------------------
        //  モーダル共通部品
        // ------------------------------------------------------------------

        /// <summary>
        /// モーダルパネルの骨格を組む。タイトル＋コンテンツ＋ボタン行。
        /// contentElement は TextField or Label（メッセージ）。
        /// </summary>
        private VisualElement BuildModalPanel(string title, VisualElement contentElement, VisualElement buttonRow)
        {
            var panel = new VisualElement();
            panel.AddToClassList("toon-modal__panel");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("toon-modal__title");

            panel.Add(titleLabel);
            panel.Add(contentElement);
            panel.Add(buttonRow);

            return panel;
        }

        /// <summary>
        /// モーダルオーバーレイを _root の最前面に追加して表示。
        /// 既にオーバーレイが出ていれば一旦消してから差し替え。
        /// onEnterAction を渡すと Enter キーでプライマリボタンと同等の動作を発火する。
        /// ESC は常にキャンセル（DismissModal）。
        /// </summary>
        private void ShowModal(VisualElement panelContent, Action onEnterAction = null)
        {
            // 既存モーダルがあれば除去（多重防止）
            DismissModal();

            _modalOverlay = new VisualElement();
            _modalOverlay.AddToClassList("toon-modal__overlay");
            _modalOverlay.Add(panelContent);

            // --- Enter / ESC キーボード操作 ---
            // デリゲートをフィールドに保持して DismissModal で確実に解除する（対称性）
            _modalEnterAction = onEnterAction;
            _onModalKeyDown = evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    _modalEnterAction?.Invoke();
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    DismissModal();
                    evt.StopPropagation();
                }
            };
            _modalOverlay.RegisterCallback(_onModalKeyDown);

            // フォーカスが無いと KeyDownEvent が届かないので、overlay 自体を focusable に。
            // 登録モーダルでは TextField.Focus で入力欄にフォーカスが行くが、
            // KeyDownEvent はバブルアップで overlay まで届くので問題なし。
            // 上書き/削除モーダルには TextField が無いので overlay 自体にフォーカスを当てる。
            _modalOverlay.focusable = true;

            // _root の最前面に追加（カラーピッカーよりも手前に来る）
            _root.Add(_modalOverlay);

            // 子にフォーカス可能要素が無い場合は overlay 自体にフォーカス
            // （上書き/削除/リセットモーダル用）。1 フレ遅延はレイアウト反映待ち。
            // 登録モーダルは nameField.Focus で上書きされる。
            _modalOverlay.schedule.Execute(() =>
            {
                if (_modalOverlay != null && _modalOverlay.focusController?.focusedElement == null)
                    _modalOverlay.Focus();
            });
        }

        /// <summary>モーダルオーバーレイを除去。KeyDownEvent の購読も解除する。</summary>
        private void DismissModal()
        {
            if (_modalOverlay == null) return;

            // キーボードイベント解除（購読と対称）
            if (_onModalKeyDown != null)
            {
                _modalOverlay.UnregisterCallback(_onModalKeyDown);
                _onModalKeyDown = null;
            }
            _modalEnterAction = null;

            _modalOverlay.RemoveFromHierarchy();
            _modalOverlay = null;
        }

        // ================================================================
        //  セクション Enable / 色窓 / ピッカー
        // ================================================================

        private void OnSectionToggleChanged(string sectionId, bool value)
        {
            _enableStates[sectionId] = value;
            if (_sections.TryGetValue(sectionId, out var sec))
                sec.SetBodyEnabled(value);
            _dirty = true;
        }

        private void OnSwatchClick(PointerDownEvent evt, string rowId)
        {
            if (evt.button != 0) return;
            if (_colorPicker == null) return;
            if (!_colorRows.TryGetValue(rowId, out var cr)) return;

            _activeColorRowId = rowId;

            var swatchRect = cr.swatch.worldBound;
            var anchorPos = new Vector2(swatchRect.xMax + 8f, swatchRect.y);

            var toonApp = _root.Q("toon-app");
            _colorPicker.Open(cr.color, anchorPos, toonApp ?? _root);

            evt.StopPropagation();
        }

        private void OnColorPickerChanged(Color color)
        {
            if (string.IsNullOrEmpty(_activeColorRowId)) return;
            if (!_colorRows.TryGetValue(_activeColorRowId, out var cr)) return;

            cr.color = color;
            cr.swatch.style.backgroundColor = color;
            cr.hexLabel.text = $"#{RuntimeColorPicker.ColorToHex(color)}";

            _dirty = true;
        }

        // ================================================================
        //  State 連携 — InitFromState
        //  ToonExporterState の値を全ウィジェットに反映する。
        //  純関数: 副作用ゼロ・Apply 呼ばない
        // ================================================================

        /// <summary>
        /// State からウィジェット値を書き戻す。
        /// SetValueWithoutNotify で _dirty の無限ループを回避。
        /// </summary>
        public void InitFromState(ToonExporterState state)
        {
            if (state == null) return;

            SetPad("shadow1.mainLightDir", state.mainLightDirection);

            // --- 影1 ---
            // shadow1 は state.shadow1.enabled の値に関わらず常に ON 表示に固定
            // （Shader に `_UseShadow1` 相当プロパティ無し）。プリセットに false が紛れてても握りつぶす
            SetSectionEnabled("shadow1", true);
            SetSlider("shadow1.threshold", state.shadow1.threshold);
            SetSlider("shadow1.softness", state.shadow1.softness);
            SetColor("shadow1.color", state.shadow1.color);
            // テクスチャ未ロードで useColorTexture=true のプリセットを読むと
            // _shadowTexture のデフォルト "black" が影ソース化して真っ黒事故になる。
            // ロード済みの時だけ state の値を採用、未ロードなら強制 false
            bool shadowTexLoaded = imageImporter != null && imageImporter.HasShadowTexture;
            SetToggle("shadow1.useColorTexture", shadowTexLoaded && state.shadow1.useColorTexture);

            // --- 影2 ---
            SetSectionEnabled("shadow2", state.shadow2.enabled);
            SetSlider("shadow2.threshold", state.shadow2.threshold);
            SetSlider("shadow2.softness", state.shadow2.softness);
            SetColor("shadow2.color", state.shadow2.color);
            SetPad("shadow2.lightDir", state.shadow2.lightDirection);

            // --- リム ---
            SetSectionEnabled("rim", state.rim.enabled);
            SetSlider("rim.width", state.rim.width);
            SetColor("rim.color", state.rim.color);

            // --- サブライト（intensity は FixedSubIntensity 固定なので UI に行なし） ---
            SetSectionEnabled("sub", state.subLight.enabled);
            SetSlider("sub.threshold", state.subLight.threshold);
            SetSlider("sub.softness", state.subLight.softness);
            SetColor("sub.color", state.subLight.color);
            SetPad("sub.lightDir", state.subLight.lightDirection);
        }

        // ================================================================
        //  State 連携 — BuildStateFromUI
        //  全ウィジェットの現在値から ToonExporterState を構築する。
        //  純関数: 副作用ゼロ。
        // ================================================================

        /// <summary>
        /// UI の現在値から State を組み立てる。
        /// </summary>
        public ToonExporterState BuildStateFromUI()
        {
            var state = new ToonExporterState();

            // --- ターゲット本数（4 固定・5 枚 PNG 出力） ---
            state.targetCount = 4;

            state.mainLightDirection = GetPad("shadow1.mainLightDir");

            // --- 影1 ---
            // Shader に `_UseShadow1` 相当プロパティが無いので State レベルで常時 true 固定
            // （プリセット保存/DeepCopy 経由で false が紛れ込む事故の二重保険）
            state.shadow1.enabled = true;
            state.shadow1.threshold = GetSlider("shadow1.threshold");
            state.shadow1.softness = GetSlider("shadow1.softness");
            state.shadow1.color = GetColor("shadow1.color");
            state.shadow1.useColorTexture = GetToggle("shadow1.useColorTexture");

            // --- 影2 ---
            state.shadow2.enabled = GetEnabled("shadow2");
            state.shadow2.threshold = GetSlider("shadow2.threshold");
            state.shadow2.softness = GetSlider("shadow2.softness");
            state.shadow2.color = GetColor("shadow2.color");
            state.shadow2.lightDirection = GetPad("shadow2.lightDir");

            // --- リム ---
            state.rim.enabled = GetEnabled("rim");
            state.rim.width = GetSlider("rim.width");
            state.rim.color = GetColor("rim.color");

            // --- サブライト（intensity は FixedSubIntensity=0.1f 固定） ---
            state.subLight.enabled = GetEnabled("sub");
            state.subLight.threshold = GetSlider("sub.threshold");
            state.subLight.softness = GetSlider("sub.softness");
            state.subLight.intensity = ToonExporterState.FixedSubIntensity;
            state.subLight.color = GetColor("sub.color");
            state.subLight.lightDirection = GetPad("sub.lightDir");

            return state;
        }

        // ================================================================
        //  InitFromState ヘルパー（SetValueWithoutNotify で無限ループ回避）
        // ================================================================

        private void SetSectionEnabled(string sectionId, bool enabled)
        {
            _enableStates[sectionId] = enabled;
            if (_sections.TryGetValue(sectionId, out var sec))
            {
                sec.SetToggleWithoutNotify(enabled);
                sec.SetBodyEnabled(enabled);
            }
        }

        private void SetSlider(string id, float value)
        {
            if (_sliders.TryGetValue(id, out var s)) s.SetValueWithoutNotify(value);
        }

        private void SetSegment(string id, int index)
        {
            if (_segments.TryGetValue(id, out var s)) s.SetValueWithoutNotify(index);
        }

        private void SetPad(string id, Vector2 value)
        {
            if (_pads.TryGetValue(id, out var p)) p.SetValueWithoutNotify(value);
        }

        private void SetColor(string id, Color color)
        {
            if (_colorRows.TryGetValue(id, out var cr))
            {
                cr.color = color;
                cr.swatch.style.backgroundColor = color;
                cr.hexLabel.text = $"#{RuntimeColorPicker.ColorToHex(color)}";
            }
        }

        private void SetToggle(string id, bool value)
        {
            if (_toggles.TryGetValue(id, out var t)) t.SetValueWithoutNotify(value);
        }

        // ================================================================
        //  BuildStateFromUI ヘルパー
        // ================================================================

        private bool GetEnabled(string sectionId) =>
            _enableStates.TryGetValue(sectionId, out var v) && v;

        private float GetSlider(string id) =>
            _sliders.TryGetValue(id, out var s) ? s.Value : 0f;

        private int GetSegment(string id) =>
            _segments.TryGetValue(id, out var s) ? s.SelectedIndex : 0;

        private Vector2 GetPad(string id) =>
            _pads.TryGetValue(id, out var p) ? p.Value : new Vector2(0.5f, 0.5f);

        private Color GetColor(string id) =>
            _colorRows.TryGetValue(id, out var cr) ? cr.color : Color.white;

        private bool GetToggle(string id) =>
            _toggles.TryGetValue(id, out var t) && t.Value;

        // ================================================================
        //  DeepCopy ラウンドトリップ検証（エディタ専用・手検証用）
        // ================================================================

#if UNITY_EDITOR
        [ContextMenu("Debug: State Roundtrip Test")]
        private void DebugRoundtripTest()
        {
            var state = BuildStateFromUI();
            var copy = state.DeepCopy();
            InitFromState(copy);
            var state2 = BuildStateFromUI();

            string json1 = JsonUtility.ToJson(state);
            string json2 = JsonUtility.ToJson(state2);

            if (json1 == json2)
                Debug.Log("[ToonExporterPanel] Roundtrip test PASSED");
            else
                Debug.LogError($"[ToonExporterPanel] Roundtrip test FAILED\nBefore: {json1}\nAfter:  {json2}");
        }
#endif

        // ================================================================
        //  タイトルバードラッグ移動
        // ================================================================

        private void OnTitlebarPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            if (_panel == null) return;

            _isDragging = true;
            _dragStartPointer = evt.position;
            _dragStartPanelPos = new Vector2(
                _panel.resolvedStyle.left,
                _panel.resolvedStyle.top
            );

            _titlebar.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnTitlebarPointerMove(PointerMoveEvent evt)
        {
            if (!_isDragging) return;
            if (_panel == null) return;

            Vector2 delta = (Vector2)evt.position - _dragStartPointer;
            float newLeft = _dragStartPanelPos.x + delta.x;
            float newTop  = _dragStartPanelPos.y + delta.y;

            ClampPanelPosition(ref newLeft, ref newTop);

            _panel.style.left = newLeft;
            _panel.style.top  = newTop;

            evt.StopPropagation();
        }

        private void OnTitlebarPointerUp(PointerUpEvent evt)
        {
            if (!_isDragging) return;

            _isDragging = false;
            _titlebar.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        // ================================================================
        //  パネル位置クランプ
        // ================================================================

        private static void ClampPanelPosition(ref float left, ref float top,
            float panelW, float panelH, float rootW, float rootH, float titlebarH)
        {
            const float keepX = 80f;

            float minLeft = -(panelW - keepX);
            float maxLeft = rootW - keepX;
            left = Mathf.Clamp(left, minLeft, maxLeft);

            float maxTop = rootH - titlebarH;
            top = Mathf.Clamp(top, 0f, maxTop);
        }

        private void ClampPanelPosition(ref float left, ref float top)
        {
            float panelW    = _panel.resolvedStyle.width;
            float panelH    = _panel.resolvedStyle.height;
            float rootW     = _root.resolvedStyle.width;
            float rootH     = _root.resolvedStyle.height;
            float titlebarH = _titlebar != null ? _titlebar.resolvedStyle.height : 32f;

            ClampPanelPosition(ref left, ref top, panelW, panelH, rootW, rootH, titlebarH);
        }

        // ================================================================
        //  動的 maxHeight
        // ================================================================

        private void OnRootGeometryChanged(GeometryChangedEvent evt)
        {
            if (_panel == null) return;
            float rootH = _root.resolvedStyle.height;
            if (float.IsNaN(rootH) || rootH <= 0f) return;

            float dynamicMax = Mathf.Min(PanelMaxHeightCap, rootH - PanelVerticalMargin);
            dynamicMax = Mathf.Max(dynamicMax, 300f);
            _panel.style.maxHeight = dynamicMax;
        }

        // ================================================================
        //  ライフサイクル — イベント解除
        // ================================================================

        private void OnDestroy()
        {
            if (_titlebar != null)
            {
                _titlebar.UnregisterCallback<PointerDownEvent>(OnTitlebarPointerDown);
                _titlebar.UnregisterCallback<PointerMoveEvent>(OnTitlebarPointerMove);
                _titlebar.UnregisterCallback<PointerUpEvent>(OnTitlebarPointerUp);
            }

            if (_root != null)
                _root.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);

            if (_colorPicker != null)
            {
                _colorPicker.onChanged -= OnColorPickerChanged;
                _colorPicker.Dispose();
            }

            if (_btnPresetAdd != null)
                _btnPresetAdd.clicked -= OnPresetAddClicked;
            if (_btnPresetDelete != null)
                _btnPresetDelete.clicked -= OnPresetDeleteClicked;
            if (_btnPresetReset != null)
                _btnPresetReset.clicked -= OnPresetResetClicked;

            if (_btnExport != null)
                _btnExport.clicked -= OnExportClicked;
            if (exportController != null && _onExportStateChanged != null)
            {
                exportController.ExportStateChanged -= _onExportStateChanged;
                _onExportStateChanged = null;
            }

            if (_btnLoadMain != null)
                _btnLoadMain.clicked -= OnLoadMainClicked;
            if (_btnLoadNormal != null)
                _btnLoadNormal.clicked -= OnLoadNormalClicked;
            if (imageImporter != null)
            {
                if (_onIllustLoaded != null)
                    imageImporter.OnIllustrationLoaded -= _onIllustLoaded;
                if (_onNormalLoaded != null)
                    imageImporter.OnNormalLoaded -= _onNormalLoaded;
                if (_onShadowTexLoaded != null)
                    imageImporter.OnShadowTextureLoaded -= _onShadowTexLoaded;
            }
            _onIllustLoaded = null;
            _onNormalLoaded = null;
            _onShadowTexLoaded = null;

            DismissModal();

            if (_onPresetNotification != null)
            {
                PresetManager.OnNotification -= _onPresetNotification;
                _onPresetNotification = null;
            }

            // ToastManager 側でも冪等に呼んでるから二重でも安全
            if (ToastManager.Instance != null)
                ToastManager.Instance.DetachLayer();

            // 辞書クリア（参照切り）
            _presetButtons.Clear();
        }
    }
}
