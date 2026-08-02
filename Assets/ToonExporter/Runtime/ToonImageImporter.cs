using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using ToonExporter.UI;
using USFB; // unity-standalone-file-browser (Sov3rain fork)

namespace ToonExporter.Runtime
{
    // =================================================================
    // ToonImageImporter — 3枚入力（イラスト / ノーマル / 影テクスチャ）の読み込み担当
    //
    //   - イラストは sRGB、ノーマルは linear で読む
    //   - Material オーナーは ToonPreviewController に一本化、Texture 差し込みは
    //     _previewController.Set*() 経由（自身は Material に触らない＝二重破棄回避）
    //   - 額縁決定（FitToImage / Feature.overrideWidth/Height）はイラスト経路のみ
    //     ノーマル・影テクスチャからは触らない
    //   - ImportLocked で書き出し中の差し替えをブロック
    // =================================================================
    [DisallowMultipleComponent]
    public class ToonImageImporter : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // Inspector 参照
        // ------------------------------------------------------------------

        [Header("参照")]
        [Tooltip("画像を貼り付ける Quad。ToonPreviewController._targetRenderer と同一 Renderer を指すこと" +
                 "（別々だとアスペクト吸着と Material 反映がズレる）")]
        [SerializeField] private Renderer targetQuad;

        [Tooltip("プレビュー用 Orthographic カメラ")]
        [SerializeField] private Camera targetCamera;

        [Tooltip("Material 反映窓口。SetIllustration/SetNormal をここ経由で呼ぶ")]
        [SerializeField] private ToonPreviewController _previewController;

        [Tooltip("URP の UniversalRendererData。ToonMRTRendererFeature の override を書き換えるのに使う")]
        [SerializeField] private UniversalRendererData _rendererData;

        [Header("表示スケール設定")]
        [Tooltip("画面に対する最大表示率（0.8 = 画面の80%まで）")]
        [Range(0.1f, 1f)]
        [SerializeField] private float screenCapRatio = 0.8f;

        [Tooltip("ウィンドウの最小幅（px）。極端な縦長対策")]
        [SerializeField] private int minWindowSize = 400;

        [Tooltip("ウィンドウの最小高さ（px）。UIパネル操作可能な最低縦幅を確保")]
        [SerializeField] private int minClientHeight = 560;

        [Header("テクスチャ設定")]
        [SerializeField] private FilterMode filterMode = FilterMode.Bilinear;

        [Header("入力制限")]
        [Tooltip("入力画像の長辺上限px。超えたらアスペクト維持で自動リサイズしてトースト通知。MRT 5本ぶんの VRAM 圧対策")]
        [SerializeField] private int maxLongSide = 4096;

        // ------------------------------------------------------------------
        // 内部保持
        // ------------------------------------------------------------------

        // 現在ロード中の3枚。連続インポート時に破棄するため保持する
        private Texture2D _currentIllust;
        private Texture2D _currentNormal;
        // テクスチャ影ソース（_shadowTexture）。額縁も Feature override も触らない
        private Texture2D _currentShadowTex;

        // ラベル動的更新用に最終ロードパスを保持
        private string _illustPath;
        private string _normalPath;
        private string _shadowTexPath;

        // Feature キャッシュ（毎フレーム走査回避）
        private ToonMRTRendererFeature _cachedFeature;

        // Quad の高さは 1 ワールドユニット固定、横をアスペクトで伸ばす
        private const float QuadBaseHeight = 1f;

        // ------------------------------------------------------------------
        // 公開プロパティ / イベント
        // ------------------------------------------------------------------

        /// <summary>
        /// true の間はインポート操作をブロックする。
        /// 書き出し Coroutine 中に画像差し替えが走ると RT 再確保と ReadPixels が
        /// 競合するため、ExportController が書き出し開始/終了で true/false する。
        /// </summary>
        public bool ImportLocked { get; set; }

        /// <summary>イラストがロード済みかどうか（書き出しガード用）</summary>
        public bool HasIllustration => _currentIllust != null;

        /// <summary>最後にロードしたイラストの幅（未ロードなら0）</summary>
        public int LoadedIllustWidth  => _currentIllust != null ? _currentIllust.width  : 0;
        /// <summary>最後にロードしたイラストの高さ（未ロードなら0）</summary>
        public int LoadedIllustHeight => _currentIllust != null ? _currentIllust.height : 0;

        /// <summary>UI ラベル動的更新用。未ロードなら空文字</summary>
        public string LoadedIllustFileName =>
            string.IsNullOrEmpty(_illustPath) ? "" : Path.GetFileName(_illustPath);

        /// <summary>UI ラベル動的更新用。未ロードなら空文字</summary>
        public string LoadedNormalFileName =>
            string.IsNullOrEmpty(_normalPath) ? "" : Path.GetFileName(_normalPath);

        /// <summary>影テクスチャがロード済みかどうか（未ロードガード用）</summary>
        public bool HasShadowTexture => _currentShadowTex != null;

        /// <summary>UI ラベル動的更新用。未ロードなら空文字</summary>
        public string LoadedShadowTexFileName =>
            string.IsNullOrEmpty(_shadowTexPath) ? "" : Path.GetFileName(_shadowTexPath);

        /// <summary>イラストロード完了通知（Panel がラベル更新＋書き出しボタン再評価に使う）</summary>
        public event Action OnIllustrationLoaded;
        /// <summary>ノーマルロード完了通知</summary>
        public event Action OnNormalLoaded;
        /// <summary>影テクスチャロード完了通知（Panel がトグル自動ON＋未ロードガード解除に使う）</summary>
        public event Action OnShadowTextureLoaded;

        // ------------------------------------------------------------------
        // ライフサイクル
        // ------------------------------------------------------------------

        private void Awake()
        {
            // _previewController 未アサインだと画像ロードしても Material に反映されない沈黙バグになる
            if (_previewController == null)
            {
                Debug.LogError(
                    "[ToonImageImporter] _previewController が未アサイン。" +
                    "画像を読み込んでもプレビューに反映されません。Inspector で ToonPreviewController をアサインして");
            }

            // targetQuad と PreviewController._targetRenderer が別 Renderer だと、
            // アスペクト吸着した Quad と Material が乗ってる Quad がズレて実行時に見た目だけ壊れる
            if (_previewController != null && targetQuad != null)
            {
                var previewRenderer = _previewController.TargetRenderer;
                if (previewRenderer != null && previewRenderer != targetQuad)
                {
                    Debug.LogWarning(
                        "[ToonImageImporter] targetQuad と ToonPreviewController._targetRenderer が別 Renderer。" +
                        "アスペクト吸着と Material 反映がズレる可能性あり。Inspector で同一 Renderer を指すよう修正して");
                }
            }
        }

        private void OnDestroy()
        {
            // Texture の破棄責務は自分（Material は触らない）
            if (_currentIllust != null)
            {
                Destroy(_currentIllust);
                _currentIllust = null;
            }
            if (_currentNormal != null)
            {
                Destroy(_currentNormal);
                _currentNormal = null;
            }
            if (_currentShadowTex != null)
            {
                Destroy(_currentShadowTex);
                _currentShadowTex = null;
            }
        }

        // ------------------------------------------------------------------
        // 公開 API：ダイアログ
        // ------------------------------------------------------------------

        /// <summary>「イラストを読み込み」ボタンから呼ぶ。ファイル選択ダイアログを開く</summary>
        public void OpenIllustrationDialog()
        {
            if (!TryEnterImport()) return;

            string path = ShowOpenDialog("イラスト画像を選択");
            if (string.IsNullOrEmpty(path)) return; // キャンセルは静かに戻る

            LoadIllustration(path);
        }

        /// <summary>「ノーマルを読み込み」ボタンから呼ぶ。ファイル選択ダイアログを開く</summary>
        public void OpenNormalDialog()
        {
            if (!TryEnterImport()) return;

            string path = ShowOpenDialog("ノーマルマップを選択");
            if (string.IsNullOrEmpty(path)) return;

            LoadNormal(path);
        }

        /// <summary>影1「影の読み込み」ボタンから呼ぶ。影テクスチャ選択ダイアログを開く</summary>
        public void OpenShadowTextureDialog()
        {
            if (!TryEnterImport()) return;

            string path = ShowOpenDialog("影テクスチャ画像を選択");
            if (string.IsNullOrEmpty(path)) return;

            LoadShadowTexture(path);
        }

        // ------------------------------------------------------------------
        // 公開 API：パス指定ロード
        // ------------------------------------------------------------------

        /// <summary>イラスト（_MainTex）を読み込んで反映する（sRGB 扱い）</summary>
        public void LoadIllustration(string path) => LoadImage(path, LoadTarget.Illustration);

        /// <summary>ノーマル（_Normal）を読み込んで反映する（linear 扱い）</summary>
        public void LoadNormal(string path) => LoadImage(path, LoadTarget.Normal);

        /// <summary>
        /// 影テクスチャ（_shadowTexture）を読み込んで反映する（sRGB 扱い）。
        /// イラストと同じ色テクスチャ扱い＝linear=false。額縁は決めない。
        /// </summary>
        public void LoadShadowTexture(string path) => LoadImage(path, LoadTarget.ShadowTexture);

        // ------------------------------------------------------------------
        // 実装本体
        // ------------------------------------------------------------------

        /// <summary>
        /// ImportLocked ガード共通処理。ロック中はトースト出して false を返す。
        /// </summary>
        private bool TryEnterImport()
        {
            if (ImportLocked)
            {
                ToastManager.ShowOrLog("書き出し中は画像を変更できません", ToastManager.ToastLevel.Warning);
                return false;
            }
            return true;
        }

        /// <summary>
        /// USFB OpenFilePanel を叩いて選択パスを返す（キャンセル時は null）。
        /// 拡張子は png/jpg/jpeg（Texture2D.LoadImage が対応してるフォーマットのみ）。
        /// </summary>
        private static string ShowOpenDialog(string title)
        {
            var extensions = new[]
            {
                new ExtensionFilter("画像ファイル", "png", "jpg", "jpeg"),
            };

            string[] paths = StandaloneFileBrowser.OpenFilePanel(title, "", extensions, false);
            if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0]))
                return null;

            return paths[0];
        }

        /// <summary>
        /// ロード先スロット。linear フラグは Normal のみ true（ノーマルの方向値を壊さない）。
        /// </summary>
        private enum LoadTarget { Illustration, Normal, ShadowTexture }

        /// <summary>
        /// パス指定で画像を読み込む共通ルート。
        /// target でロード時の linear フラグ・リサイズ経路・反映先を切り替える。
        /// </summary>
        private void LoadImage(string path, LoadTarget target)
        {
            if (!TryEnterImport()) return;

            // ノーマルだけ linear 扱い。イラストと影テクスチャは sRGB（色テクスチャ）。
            bool isNormal = target == LoadTarget.Normal;

            if (!File.Exists(path))
            {
                Debug.LogError($"[ToonImageImporter] ファイルが見つからない: {path}");
                ToastManager.ShowOrLog(
                    $"ファイルが見つかりません: {Path.GetFileName(path)}",
                    ToastManager.ToastLevel.Error);
                return;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException e)
            {
                Debug.LogError($"[ToonImageImporter] 読み込み失敗: {e.Message}");
                ToastManager.ShowOrLog(
                    $"ファイルの読み込みに失敗しました: {e.Message}",
                    ToastManager.ToastLevel.Error);
                return;
            }

            // linear フラグ：
            //   isNormal=true  → GPU サンプラーが sRGB→Linear 変換しない = ノーマル方向値が壊れない
            //   isNormal=false → sRGB テクスチャ扱い（Linear プロジェクトでも色味合う）
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false, linear: isNormal);

            if (!tex.LoadImage(bytes))
            {
                ToastManager.ShowOrLog(
                    $"画像の読み込みに失敗しました: {Path.GetFileName(path)}",
                    ToastManager.ToastLevel.Error);
                Destroy(tex);
                return;
            }

            // 長辺キャップ。MRT 5本ぶんの VRAM 圧対策
            int longSide = Mathf.Max(tex.width, tex.height);
            if (longSide > maxLongSide)
            {
                int origW = tex.width;
                int origH = tex.height;
                float scale = (float)maxLongSide / longSide;
                int newW = Mathf.Max(1, Mathf.RoundToInt(origW * scale));
                int newH = Mathf.Max(1, Mathf.RoundToInt(origH * scale));

                Texture2D resized = ResizeTexture(tex, newW, newH, isNormal);
                Destroy(tex);
                tex = resized;

                ToastManager.ShowOrLog(
                    $"入力画像が大きいので {newW}x{newH} にリサイズしました（元: {origW}x{origH}）",
                    ToastManager.ToastLevel.Info);
            }

            tex.filterMode = filterMode;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(updateMipmaps: false);

            switch (target)
            {
                case LoadTarget.Normal:        ApplyNormal(tex, path);        break;
                case LoadTarget.ShadowTexture: ApplyShadowTexture(tex, path);  break;
                default:                       ApplyIllustration(tex, path);   break;
            }
        }

        /// <summary>
        /// GPU Blit 経由の Texture2D リサイズ。
        /// isNormal の場合は RT と最終 Texture2D の両方を linear で確保しないと
        /// GPU サンプラーが sRGB デコード適用して法線が壊れる。
        /// 呼び出し側で source の Destroy 責務を持つ。
        /// </summary>
        private static Texture2D ResizeTexture(Texture2D source, int newW, int newH, bool isNormal)
        {
            source.filterMode = FilterMode.Bilinear;

            var readwrite = isNormal
                ? RenderTextureReadWrite.Linear
                : RenderTextureReadWrite.sRGB;

            RenderTexture rt = RenderTexture.GetTemporary(
                newW, newH, 0,
                RenderTextureFormat.ARGB32,
                readwrite);
            rt.filterMode = FilterMode.Bilinear;

            RenderTexture prevActive = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;

                // 最終 Texture2D の linear フラグも isNormal 追従
                var result = new Texture2D(newW, newH, TextureFormat.RGBA32, mipChain: false, linear: isNormal);
                result.ReadPixels(new Rect(0, 0, newW, newH), 0, 0);
                result.Apply(updateMipmaps: false);
                return result;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // ------------------------------------------------------------------
        // Apply — Texture 差し込み + 額縁 + Feature override
        // ------------------------------------------------------------------

        /// <summary>
        /// イラスト（_MainTex）反映。額縁決定と Feature override 更新はここでだけ実施。
        /// ノーマル解像度で額縁書き換えると絵が歪むので厳密にイラスト経路限定。
        /// </summary>
        private void ApplyIllustration(Texture2D tex, string path)
        {
            if (_currentIllust != null)
            {
                Destroy(_currentIllust);
                _currentIllust = null;
            }

            _currentIllust = tex;
            _illustPath = path;

            _previewController?.SetIllustration(tex);

            // 額縁決定（Quad スケール・ウィンドウ吸着・カメラ aspect）はイラストのみ
            FitToImage(tex.width, tex.height);

            // MRT RT 解像度をイラストに追従（override 書き換えのみ、再確保は RenderGraph が次フレーム自動で）
            var feature = GetFeature();
            if (feature != null)
            {
                feature.overrideWidth  = tex.width;
                feature.overrideHeight = tex.height;
            }
            else
            {
                ToastManager.ShowOrLog(
                    "書き出し解像度が入力画像に連動しません（RendererData 未設定 or Feature 未追加）",
                    ToastManager.ToastLevel.Warning);
            }

            WarnIfResolutionMismatch();

            OnIllustrationLoaded?.Invoke();
        }

        /// <summary>
        /// ノーマル（_Normal）反映。額縁・Feature override には一切触らない。
        /// </summary>
        private void ApplyNormal(Texture2D tex, string path)
        {
            if (_currentNormal != null)
            {
                Destroy(_currentNormal);
                _currentNormal = null;
            }

            _currentNormal = tex;
            _normalPath = path;

            _previewController?.SetNormal(tex);

            WarnIfResolutionMismatch();

            OnNormalLoaded?.Invoke();
        }

        /// <summary>
        /// 影テクスチャ（_shadowTexture）反映。ApplyNormal と同型で、額縁・Feature override には触らない。
        /// UV 0..1 でサンプルされる影ソースなので解像度不一致もブロックしない（イラスト⇔ノーマル専用）。
        /// </summary>
        private void ApplyShadowTexture(Texture2D tex, string path)
        {
            if (_currentShadowTex != null)
            {
                Destroy(_currentShadowTex);
                _currentShadowTex = null;
            }

            _currentShadowTex = tex;
            _shadowTexPath = path;

            _previewController?.SetShadowTexture(tex);

            OnShadowTextureLoaded?.Invoke();
        }

        /// <summary>
        /// イラストとノーマルの解像度が食い違ってたら Warning トースト。
        /// UV は 0..1 共通サンプルなので動作自体はする＝ブロックせず注意喚起のみ。
        /// </summary>
        private void WarnIfResolutionMismatch()
        {
            if (_currentIllust == null || _currentNormal == null) return;

            if (_currentIllust.width != _currentNormal.width ||
                _currentIllust.height != _currentNormal.height)
            {
                ToastManager.ShowOrLog(
                    $"イラスト({_currentIllust.width}x{_currentIllust.height}) と " +
                    $"ノーマル({_currentNormal.width}x{_currentNormal.height}) の解像度が違います。" +
                    "書き出しはイラスト解像度で行われます",
                    ToastManager.ToastLevel.Warning);
            }
        }

        // ------------------------------------------------------------------
        // 額縁決定（イラスト経路のみ呼ばれる・ノーマルからは呼ばない）
        // ------------------------------------------------------------------

        /// <summary>
        /// 画像アスペクトに合わせて Quad スケール / ウィンドウ / カメラを吸着させる（1枚基準）。
        /// </summary>
        private void FitToImage(int imgWidth, int imgHeight)
        {
            if (imgWidth <= 0 || imgHeight <= 0) return;

            float aspect = (float)imgWidth / imgHeight;

            // --- Quad スケール（高さ基準で横をアスペクトで伸ばす）---
            if (targetQuad == null)
            {
                Debug.LogWarning("[ToonImageImporter] targetQuad が未設定。Inspector でアサインして");
            }
            else
            {
                var t = targetQuad.transform;
                t.localScale = new Vector3(QuadBaseHeight * aspect, QuadBaseHeight, 1f);
                t.localPosition = Vector3.zero;
            }

            // --- 表示ウィンドウサイズ算出 ---
            int screenW = Display.main.systemWidth;
            int screenH = Display.main.systemHeight;

            int maxW = Mathf.RoundToInt(screenW * screenCapRatio);
            int maxH = Mathf.RoundToInt(screenH * screenCapRatio);

            float scale = Mathf.Min(
                (float)maxW / imgWidth,
                (float)maxH / imgHeight,
                1f // 拡大しない（元解像度以上に引き伸ばさない）
            );

            int winW = Mathf.RoundToInt(imgWidth * scale);
            int winH = Mathf.RoundToInt(imgHeight * scale);

            winW = Mathf.Max(winW, minWindowSize);
            winH = Mathf.Max(winH, minClientHeight);

            // --- ウィンドウ吸着 ---
            Screen.SetResolution(winW, winH, FullScreenMode.Windowed);

            // SetResolution は非同期で aspect が旧値のままなことがあるので明示セット
            if (targetCamera != null)
            {
                targetCamera.aspect = aspect;
            }
            else
            {
                Debug.LogWarning("[ToonImageImporter] targetCamera が未設定。Inspector でアサインして");
            }

            // --- Orthographic Size ---
            // Quad 高さ = QuadBaseHeight ワールドユニット。orthographicSize は「画面の縦半分の
            // ワールド高さ」なので、Quad 全体（縦1）が収まるように 0.5 にする。
            if (targetCamera != null && targetCamera.orthographic)
            {
                targetCamera.orthographicSize = QuadBaseHeight * 0.5f;
            }
        }

        // ------------------------------------------------------------------
        // Feature 取得
        // ------------------------------------------------------------------

        private ToonMRTRendererFeature GetFeature()
        {
            if (_cachedFeature != null) return _cachedFeature;

            if (_rendererData == null)
            {
                Debug.LogError("[ToonImageImporter] RendererData が未設定。Inspector で UniversalRendererData をアサインして");
                return null;
            }

            _cachedFeature = ToonMRTRendererFeature.FindIn(_rendererData);
            if (_cachedFeature == null)
            {
                Debug.LogError("[ToonImageImporter] ToonMRTRendererFeature が見つからない。" +
                               "RendererData に Feature が追加されているか確認して");
            }
            return _cachedFeature;
        }
    }
}
