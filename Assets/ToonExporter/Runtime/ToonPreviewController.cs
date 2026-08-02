using UnityEngine;
using ToonExporter.Core;
using ToonExporter.UI;

namespace ToonExporter.Runtime
{
    // =================================================================
    // ToonPreviewController — UI State → Material のライブ反映ブリッジ
    //
    // Panel.StateChanged を購読して、Renderer.material にライブ反映する。
    // プレビュー Pass1 と MRT 書き出し Pass2 が同一 CBUFFER を共有してるので、
    // マテリアル1本を書き換えれば「見た目」と「書き出し中身」が同時に整う。
    //
    // ═══════════════════════════════════════════════════════════════
    // 絶対ルール
    // ═══════════════════════════════════════════════════════════════
    //   🔴 sharedMaterial を触るな          → .mat アセット汚染で絵描き環境破壊
    //   🔴 MaterialPropertyBlock を使うな   → プレビュー Pass1 でインスタンス化濁る
    //   🔴 差分検出を書くな                 → Panel.Update() で 1 フレーム 1 回に
    //                                         coalesce 済み。BuildStateFromUI は純関数
    //   🟢 購読解除は対称に                 → OnDestroy で -= 忘れずに
    // ═══════════════════════════════════════════════════════════════
    //
    // asmdef 位置: Runtime。Material 実体を触るのはランタイム責務。
    // Core には置かない（Core → UI/Runtime 依存禁止の鉄則）。
    // =================================================================
    [DisallowMultipleComponent]
    public class ToonPreviewController : MonoBehaviour
    {
        // Vector2Pad(0..1) → Shader Vector4 の z 成分。実機の見え感で調整可能な形で持つ
        private const float DIRECTION_Z_DEFAULT = 1.0f;

        [Header("Target")]
        [Tooltip("トゥーンシェーダーが貼られた Quad の Renderer。ここのマテリアルにライブ反映する")]
        [SerializeField] private Renderer _targetRenderer;

        [Header("Panel Binding")]
        [Tooltip("Panel を持つ GameObject。未アサインなら同 GO を GetComponent で探す")]
        [SerializeField] private ToonExporterPanel _panel;

        // renderer.material はインスタンス（実行中だけの複製）。
        // Awake で1回掴んでキャッシュ。以降は SetFloat/SetColor で直に上書きする。
        private Material _material;

        // 購読解除のためにデリゲート参照を保持（-= の対称性）
        private System.Action _onStateChanged;

        // ---------------------------------------------------------------
        // ライフサイクル
        // ---------------------------------------------------------------

        private void Awake()
        {
            if (_targetRenderer == null)
            {
                Debug.LogError("[ToonPreviewController] _targetRenderer が未アサイン。Inspector でアサインして");
                return;
            }

            // 🔴 sharedMaterial 禁止。renderer.material はインスタンス複製を返す。
            // 実行中だけ生きるので .mat アセットに変更が焼き付かない=絵描き環境が汚れない。
            _material = _targetRenderer.material;
        }

        private void Start()
        {
            // Panel 未アサインなら同 GO からフォールバック取得
            if (_panel == null) _panel = GetComponent<ToonExporterPanel>();
            if (_panel == null)
            {
                Debug.LogWarning("[ToonPreviewController] ToonExporterPanel が見つからない。ライブ反映は動きません");
                return;
            }

            // full state 流し（差分検出しない）。Panel.Update() の _dirty→StateChanged が
            // 1 フレーム 1 回に coalesce 済み。BuildStateFromUI は副作用ゼロの純関数
            _onStateChanged = () => ApplyToMaterial(_panel.BuildStateFromUI());
            _panel.StateChanged += _onStateChanged;

            // 起動時点でプレビューと Inspector 値が食い違わないよう初回同期
            _onStateChanged.Invoke();
        }

        private void OnDestroy()
        {
            if (_panel != null && _onStateChanged != null)
            {
                _panel.StateChanged -= _onStateChanged;
            }
            _onStateChanged = null;

            // renderer.material のインスタンスはシーン破棄で通常解放されるが、対称性の作法として明示 Destroy
            if (_material != null)
            {
                Destroy(_material);
                _material = null;
            }
        }

        // ---------------------------------------------------------------
        // ApplyToMaterial — State を Material に全載せ反映
        // ---------------------------------------------------------------

        /// <summary>
        /// State をマテリアルに全載せ反映する。差分検出はしない
        /// （SetFloat/SetColor × 20 ちょいは激軽なので都度全載せで問題なし）。
        ///
        /// ExportController.ExportCoroutine の書き出し直前からも冪等呼び出しされる
        /// （ライブ購読の状態に依存せず書き出しの正しさを独立担保）。
        /// </summary>
        public void ApplyToMaterial(ToonExporterState state)
        {
            // 無言 return: Awake/Start で既にエラー/警告ログ出してるので二重ログ回避
            if (_material == null || state == null) return;

            // ─── Shadow1（enabled は Shader 未対応・常時 ON 相当で無視） ───
            _material.SetFloat("_threshold",       state.shadow1.threshold);
            _material.SetFloat("_softness",        state.shadow1.softness);
            _material.SetColor("_Color",           state.shadow1.color);
            _material.SetFloat("_UseColorTexture", state.shadow1.useColorTexture ? 1f : 0f);

            // ─── Shadow2 ───
            _material.SetFloat("_UseShadow2",       state.shadow2.enabled ? 1f : 0f);
            _material.SetFloat("_shadow2Threshold", state.shadow2.threshold);
            _material.SetFloat("_shadow2Softness",  state.shadow2.softness);
            _material.SetColor("_shadow2Color",     state.shadow2.color);
            _material.SetVector("_shadow2Light",    Pad01ToShader(state.shadow2.lightDirection, flipXY: true));

            // ─── Rim ───
            _material.SetFloat("_useLimlight",  state.rim.enabled ? 1f : 0f);
            _material.SetFloat("_limLightWidth", state.rim.width);
            _material.SetColor("_rimColor",      state.rim.color);

            // ─── SubLight ───
            _material.SetFloat("_useSublight",       state.subLight.enabled ? 1f : 0f);
            _material.SetFloat("_subThreshold",      state.subLight.threshold);
            _material.SetFloat("_subSoftness",       state.subLight.softness);
            _material.SetFloat("_SubColorIntensity", state.subLight.intensity);
            _material.SetColor("_SubColor",          state.subLight.color);
            _material.SetVector("_sublight",         Pad01ToShader(state.subLight.lightDirection));

            // ─── Settings（メイン方向） ───
            _material.SetVector("_Light",            Pad01ToShader(state.mainLightDirection, flipXY: true));
        }

        // ---------------------------------------------------------------
        // 画像入力ブリッジ
        // ---------------------------------------------------------------
        //
        // ToonImageImporter がロードした Texture2D を Material に差し込む窓口。
        // Material インスタンスのオーナーはこのクラス一本に統一（二重破棄回避）。
        // テクスチャは State じゃなくプリセット非対象なので ApplyToMaterial とは独立経路。

        /// <summary>
        /// イラスト(_MainTex) 差し込み。tex=null で "white" デフォルトに戻る挙動は
        /// Material.SetTexture の仕様任せ（Shader 側の "white" フォールバックが効く）。
        /// </summary>
        public void SetIllustration(Texture2D tex)
        {
            if (_material == null) return;
            _material.SetTexture("_MainTex", tex);
        }

        /// <summary>
        /// ノーマル(_Normal) 差し込み。tex=null で "bump"（フラット法線）に戻る。
        /// Shader 側は UnpackNormalRGB なので生 PNG ノーマル前提。
        /// </summary>
        public void SetNormal(Texture2D tex)
        {
            if (_material == null) return;
            _material.SetTexture("_Normal", tex);
        }

        /// <summary>
        /// 影テクスチャ(_shadowTexture) 差し込み。
        /// useColorTexture=1 の時に影1の影ソースになる（ComputeShadow1 の分岐先）。
        /// tex=null で Shader 側デフォルトの "black" に戻る。
        /// </summary>
        public void SetShadowTexture(Texture2D tex)
        {
            if (_material == null) return;
            _material.SetTexture("_shadowTexture", tex);
        }

        /// <summary>
        /// ToonImageImporter の targetQuad と一致するかの検証用に公開。
        /// </summary>
        public Renderer TargetRenderer => _targetRenderer;

        // ---------------------------------------------------------------
        // Pad01ToShader — Vector2Pad(0..1) → Shader Vector4 変換
        // ---------------------------------------------------------------

        /// <summary>
        /// Vector2Pad の 0..1 UI 座標を、シェーダーの Vector4(x, y, z, w) 方向ベクトルに変換する。
        ///
        /// 変換式：
        ///   x = pad.x * 2 - 1     // 0..1 → -1..1
        ///   y = pad.y * 2 - 1     // 0..1 → -1..1
        ///   z = DIRECTION_Z_DEFAULT
        ///   w = 0
        ///   flipXY=true なら x,y の符号を反転（UI と光源方向感覚を合わせる用）
        ///
        /// 対象3プロパティで使用：
        ///   `_Light`        → flipXY: true  （UI 上に光→光源も上から来る感覚に合わせる）
        ///   `_shadow2Light` → flipXY: true  （メインと同じ）
        ///   `_sublight`     → flipXY: false （シェーダー既定 (-1, 0.3, ...) の座標系がメインと逆向きで、
        ///                                      反転なしがちょうど合う）
        /// </summary>
        private static Vector4 Pad01ToShader(Vector2 pad, bool flipXY = false)
        {
            float x = pad.x * 2f - 1f;
            float y = pad.y * 2f - 1f;
            if (flipXY)
            {
                x = -x;
                y = -y;
            }
            return new Vector4(x, y, DIRECTION_Z_DEFAULT, 0f);
        }
    }
}
