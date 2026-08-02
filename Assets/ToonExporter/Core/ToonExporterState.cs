using UnityEngine;

namespace ToonExporter.Core
{
    // =================================================================
    // ToonExporterState — トゥーンレイヤー書き出しツール State 層
    //
    // 全スロットパラメータを保持する POCO 群。作法:
    //   - [System.Serializable] の POCO 分割
    //   - DeepCopy (JsonUtility) パターン
    //   - LatestVersion 定数（マイグレーション用）
    //
    // ═══════════════════════════════════════════════════════════════
    // Shader プロパティ ⇄ State フィールド 対応表
    // ═══════════════════════════════════════════════════════════════
    //
    // [Shadow1State]
    //   enabled            ← UI トグル（Shader 側に対応プロパティ無し・常時 ON 相当）
    //   threshold          ← _threshold         Range(0, 1.2)  default 0.8
    //   softness           ← _softness          Range(0, 1)    default 0.05
    //   color              ← _Color             Color          default (0, 0, 0, 1)
    //   useColorTexture    ← _UseColorTexture   Toggle(Float)  default 0
    //
    // [Shadow2State]
    //   enabled            ← _UseShadow2        Toggle(Float)  default 0
    //   threshold          ← _shadow2Threshold  Range(0, 1.2)  default 0.5
    //   softness           ← _shadow2Softness   Range(0, 1)    default 0.05
    //   color              ← _shadow2Color      Color          default (0, 0, 0, 1)
    //   lightDirection     ← _shadow2Light      Vector         Pad(0.5, 0.5) → shader (0, 0, 1)
    //
    // [RimState]
    //   enabled            ← _useLimlight       Toggle(Float)  default 0
    //   width              ← _limLightWidth     Range(0, 1)    default 0.5
    //   color              ← _rimColor          Color          default (1, 1, 1, 1)
    //
    // [SubLightState]
    //   enabled            ← _useSublight       Toggle(Float)  default 0
    //   threshold          ← _subThreshold      Range(0, 1.2)  default 0.4
    //   softness           ← _subSoftness       Range(0, 1)    default 0.05
    //   intensity          ← _SubColorIntensity Range(0, 1)    FixedSubIntensity=0.1f 固定
    //   color              ← _SubColor          Color          default (0.345, 0.557, 1, 1) (#588EFF)
    //   lightDirection     ← _sublight          Vector         Pad(0.5, 0.5) → shader (0, 0, 1)
    //
    // [ToonExporterState]
    //   mainLightDirection ← _Light             Vector         Pad(0.5, 0.5) → shader (0, 0, 1)
    //   targetCount        ← UI 専用（4 固定）
    //
    // Vector2Pad 値 → Shader 3D 方向ベクトルの変換式（ToonPreviewController.Pad01ToShader）:
    //   shaderX = padValue.x * 2 - 1   (0..1 → -1..1)
    //   shaderY = padValue.y * 2 - 1   (0..1 → -1..1)
    //   shaderZ = DIRECTION_Z_DEFAULT (= 1.0f 固定)
    //   shaderW = 0
    //   flipXY オプション: _Light / _shadow2Light は反転あり（UI上に光→光源も上感覚合わせ）、
    //                      _sublight は反転なし（シェーダー既定の座標系がメインと逆向きなため）
    // =================================================================

    [System.Serializable]
    public class Shadow1State
    {
        // threshold=0.8 は「法線が 36.9° 以上傾いたら影」の意（NdotL=cosθ より）
        // color 黒 + SoftLight で base² 相当まで沈む
        public bool enabled = true;
        public float threshold = 0.8f;
        public float softness = 0.05f;
        public Color color = Color.black;
        public bool useColorTexture;
    }

    [System.Serializable]
    public class Shadow2State
    {
        // threshold=0.5 は「60° 以上傾いたら影」の意
        public bool enabled = false;
        public float threshold = 0.5f;
        public float softness = 0.05f;
        public Color color = Color.black;
        // Pad(0.5, 0.5) = 正面（shader (0, 0, 1) 相当）
        public Vector2 lightDirection = new(0.5f, 0.5f);
    }

    [System.Serializable]
    public class RimState
    {
        public bool enabled = false;
        public float width = 0.5f;
        public Color color = Color.white;
    }

    [System.Serializable]
    public class SubLightState
    {
        // #588EFF 青系＝環境光想定
        public bool enabled = false;
        public float threshold = 0.4f;
        public float softness = 0.05f;
        public float intensity = 0.1f;
        public Color color = new(0.3450980f, 0.5568627f, 1f, 1f); // #588EFF
        public Vector2 lightDirection = new(0.5f, 0.5f);
    }

    [System.Serializable]
    public class ToonExporterState
    {
        /// <summary>Preset フォーマットの最新バージョン</summary>
        public const int LatestVersion = 1;

        /// <summary>
        /// サブライトの強さ (intensity) 固定値。UI 行は撤去済みで
        /// BuildStateFromUI で常時この値を書き込む。
        /// Shader 側 `_SubColorIntensity` は残存＝供給を止める意味ではなく、
        /// ユーザーに触らせない意味の固定化。
        /// </summary>
        public const float FixedSubIntensity = 0.1f;

        public int version = LatestVersion;

        public Shadow1State shadow1 = new();
        public Shadow2State shadow2 = new();
        public RimState rim = new();
        public SubLightState subLight = new();

        public Vector2 mainLightDirection = new(0.5f, 0.5f);

        /// <summary>
        /// 書き出しターゲット本数（3 or 4）。
        /// 3 = Shadow1 + Shadow2 + Rim、4 = + SubLight。
        /// Comp（カンプ画像）は本数に含めず常に出力。
        /// </summary>
        public int targetCount = 4;

        /// <summary>
        /// JsonUtility による Deep Copy。パフォーマンスより確実性優先。
        /// </summary>
        public ToonExporterState DeepCopy()
        {
            string json = JsonUtility.ToJson(this);
            return JsonUtility.FromJson<ToonExporterState>(json);
        }
    }
}
