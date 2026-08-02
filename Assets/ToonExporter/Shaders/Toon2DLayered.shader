Shader "ToonExporter/Toon2DLayered"
{
    // =================================================================
    // Toon2DLayered — 本番シェーダー
    //
    // - Universal Unlit + Quad 描画
    // - 2パス構造:
    //     Pass 1 "ToonPreview" (UniversalForward) → 全層合成カラーをフレームバッファへ
    //     Pass 2 "ToonMRT"     (LightMode=ToonMRT) → SV_Target0..4 に各層マスク×色＋カンプ
    // - 両パスで ToonLayers.hlsl の ComputeToonLayers() を呼ぶ
    //   → プレビュー = 書き出し の数学的一致保証
    // - CBUFFER 20+ 項目 は SubShader 内共通宣言（1シェーダー内2パス構造）
    // =================================================================

    Properties
    {
        // --- ベース ---
        _MainTex           ("Main Texture (イラスト本体)",  2D)      = "white" {}
        _shadowTexture     ("Shadow Texture (影領域用)",     2D)      = "black" {}
        _Normal            ("Normal Map (対応法線)",         2D)      = "bump"  {}
        [Toggle] _UseColorTexture ("Use shadowTexture as Shadow Source", Float) = 0
        _Color             ("Shadow Base Color",            Color)   = (0, 0, 0, 1)
        _Light             ("Main Light Dir",               Vector)  = (0, 0, 1, 0)
        _threshold         ("Shadow1 Threshold",            Range(0, 1.2)) = 0.8
        _softness          ("Shadow1 Edge Softness",        Range(0, 1))  = 0.05

        // --- リム ---
        [Toggle] _useLimlight ("Use Rim Light", Float) = 0
        _limLightWidth     ("Rim Light Width",              Range(0, 1))  = 0.5
        _rimColor          ("Rim Color",                    Color)         = (1, 1, 1, 1)

        // --- 影2 ---
        [Toggle] _UseShadow2 ("Use Shadow2", Float) = 0
        _shadow2Light      ("Shadow2 Light Dir",            Vector)  = (0, 0, 1, 0)
        _shadow2Color      ("Shadow2 Color",                Color)   = (0, 0, 0, 1)
        _shadow2Threshold   ("Shadow2 Threshold",            Range(0, 1.2)) = 0.5
        // 影2のエッジぼかしは影1(_softness)から独立。ライト方向が別なのにボケ幅共有だと歪むため
        _shadow2Softness   ("Shadow2 Edge Softness",         Range(0, 1))  = 0.05

        // --- サブライト ---
        [Toggle] _useSublight ("Use Sub Light", Float) = 0
        _sublight          ("Sub Light Dir",                Vector)  = (0, 0, 1, 0)
        _subThreshold      ("Sub Threshold",                Range(0, 1.2)) = 0.4
        _subSoftness       ("Sub Softness",                 Range(0, 1)) = 0.05
        _SubColor          ("Sub Color",                    Color)   = (1, 0.85, 0.5, 1)
        _SubColorIntensity ("Sub Intensity",                Range(0, 1)) = 0.1
    }

    SubShader
    {
        // 半透明 Quad が主用途
        Tags
        {
            "RenderType"      = "Transparent"
            "Queue"           = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
        }

        // =============================================================
        // 両パス共通の HLSL 宣言
        // - CBUFFER
        // - テクスチャ/サンプラー
        // - Attributes / Varyings
        // - vert / BuildToonParams
        // 各 Pass の HLSLPROGRAM から #include で取り込む
        // =============================================================
        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "ToonLayers.hlsl"

            // -----------------------------------------------------------
            // CBUFFER — 20+ プロパティ共通宣言（両パスで同じ値を参照）
            // -----------------------------------------------------------
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _MainTex_TexelSize;

                float  _UseColorTexture;
                float4 _Color;
                float4 _Light;
                float  _threshold;
                float  _softness;

                float  _useLimlight;
                float  _limLightWidth;
                float4 _rimColor;

                float  _UseShadow2;
                float4 _shadow2Light;
                float4 _shadow2Color;
                float  _shadow2Threshold;
                float  _shadow2Softness;   // 影2専用ぼかし（影1 _softness から独立）

                float  _useSublight;
                float4 _sublight;
                float  _subThreshold;
                float  _subSoftness;
                float4 _SubColor;
                float  _SubColorIntensity;
            CBUFFER_END

            TEXTURE2D(_MainTex);        SAMPLER(sampler_MainTex);
            TEXTURE2D(_shadowTexture);  SAMPLER(sampler_shadowTexture);
            TEXTURE2D(_Normal);         SAMPLER(sampler_Normal);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionHCS = posInputs.positionCS;
                output.uv          = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            // -----------------------------------------------------------
            // fragment 前処理: テクスチャサンプル + ToonParams 構築
            // 両パスの frag から共通で呼ぶ
            // - Normal は Quad 平面前提でタンジェント基底固定 → normalize のみ
            //   ワールド変換/TBN 再構成は不要
            //   運用ルール: Quad は常にカメラ正対・回転なし
            // - リムの UV シフト量は rimDir=L 抽象化
            // -----------------------------------------------------------
            void BuildToonParams(float2 uv, out ToonParams p, out float3 normal)
            {
                // --- テクスチャサンプル ---
                float4 mainTexColor   = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                float4 shadowTexColor = SAMPLE_TEXTURE2D(_shadowTexture, sampler_shadowTexture, uv);
                float4 normalSample   = SAMPLE_TEXTURE2D(_Normal, sampler_Normal, uv);

                // Quad 平面前提 → タンジェント空間 ≒ スクリーン空間で使い回し
                //
                // ランタイム LoadImage した生ノーマル PNG は RGB=XYZ 直エンコード。
                // UnpackNormal は DXT5nm 前提で ag チャンネル復元するため、生 PNG だと R 成分が
                // 捨てられて壊れる。UnpackNormalRGB は生 RGB を 2*c-1 で直接展開する素直な関数。
                // 補足: _Normal="bump" プレースホルダー(0.5,0.5,1.0) も UnpackNormalRGB で
                // (0,0,1)=正面フラット法線として正しく復元される（ノーマル未ロード時互換）。
                normal = normalize(UnpackNormalRGB(normalSample));

                // --- リム UV シフト用の事前サンプル ---
                float3 rimDir     = normalize(_Light.xyz);
                float shift      = float(_limLightWidth * -0.02);   // 係数 -0.02
                float3 shift3     = float3(shift, shift, 1) * rimDir;    // .z は捨てられる
                float2 shiftedUV  = uv + shift3.xy;
                float  baseAlpha    = mainTexColor.a;
                float  shiftedAlpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, shiftedUV).a;

                // --- ToonParams 詰め込み ---
                p.mainTexColor      = mainTexColor;
                p.shadowTexColor    = shadowTexColor;

                p.lightDir          = _Light.xyz;
                p.threshold         = _threshold;
                p.softness          = _softness;
                p.shadowColor       = _Color;
                p.useColorTexture   = _UseColorTexture;

                p.useShadow2        = _UseShadow2;
                p.shadow2LightDir   = _shadow2Light.xyz;
                p.shadow2Threshold  = _shadow2Threshold;
                p.shadow2Softness   = _shadow2Softness;   // 埋め忘れると未初期化読み出しで無音バグる箇所
                p.shadow2Color      = _shadow2Color;

                p.useLimlight       = _useLimlight;
                p.limLightWidth     = _limLightWidth;
                p.rimColor          = _rimColor;
                p.baseAlpha         = baseAlpha;
                p.shiftedAlpha      = shiftedAlpha;

                p.useSublight       = _useSublight;
                p.subLightDir       = _sublight.xyz;
                p.subThreshold      = _subThreshold;
                p.subSoftness       = _subSoftness;
                p.subColor          = _SubColor;
                p.subColorIntensity = _SubColorIntensity;
            }
        ENDHLSL

        // =============================================================
        // Pass 1 — プレビュー用（UniversalForward）
        //   通常カメラが拾う。Quad に貼って絵描きに「最終見た目」を見せる
        //   Blend SrcAlpha OneMinusSrcAlpha = 通常の半透明合成
        // =============================================================
        Pass
        {
            Name "ToonPreview"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragPreview
            #pragma multi_compile_instancing

            float4 fragPreview(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                ToonParams p;
                float3 normal;
                BuildToonParams(input.uv, p, normal);

                LayerResult r = ComputeToonLayers(p, normal);
                return r.composited;
            }
            ENDHLSL
        }

        // =============================================================
        // Pass 2 — MRT書き出し用（LightMode=ToonMRT）
        //   通常カメラは拾わない。自前 RendererFeature からのみ呼ばれる
        //   Blend One Zero = 生値を各 RT に焼く（候補B の必須条件）
        //
        //   注意: 将来ターゲット別ブレンド変更時は
        //         Blend 0 One Zero, Blend 1 One Zero, ... と個別指定要
        // =============================================================
        Pass
        {
            Name "ToonMRT"
            Tags { "LightMode" = "ToonMRT" }

            Blend One Zero
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragMRT
            #pragma multi_compile_instancing

            // SV_Target4 = カンプ画像（全層合成後・絵描き復元手引き）
            struct FragmentOutput
            {
                float4 shadow1 : SV_Target0;
                float4 shadow2 : SV_Target1;
                float4 rim     : SV_Target2;
                float4 sub     : SV_Target3;
                float4 comp    : SV_Target4;
            };

            FragmentOutput fragMRT(Varyings input)
            {
                UNITY_SETUP_INSTANCE_ID(input);

                ToonParams p;
                float3 normal;
                BuildToonParams(input.uv, p, normal);

                LayerResult r = ComputeToonLayers(p, normal);

                FragmentOutput o;
                o.shadow1 = r.shadow1;
                o.shadow2 = r.shadow2;
                o.rim     = r.rim;
                o.sub     = r.sub;
                o.comp    = r.composited;
                return o;
            }
            ENDHLSL
        }
    }

    // フォールバックなし（本番シェーダーは URP 前提固定）
}
