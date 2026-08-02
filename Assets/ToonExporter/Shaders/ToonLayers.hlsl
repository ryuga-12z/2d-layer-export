#ifndef TOON_LAYERS_INCLUDED
#define TOON_LAYERS_INCLUDED

// =====================================================================
// ToonLayers.hlsl
//
// 本番シェーダー用の「層計算共有ヘッダ」。
// - SV_Target も CBUFFER もここには書かない（パス非依存）
// - Toon2DLayered.shader の Pass 1 (プレビュー) / Pass 2 (MRT) 両方から include
// - LayerResult に「各層マスク × 色」+「合成後カラー」を1回で返すことで
//   プレビュー = 書き出し の数学的一致を保証
// =====================================================================

// ---------------------------------------------------------------------
// 入力パラメータ構造体
// CBUFFER の値をフラグメント側でこの struct に詰め替えて渡す。
// テクスチャ/サンプラーは HLSL の言語仕様上 struct に入れられないので、
// フラグメント側で SAMPLE_TEXTURE2D したカラーを渡す運用にする。
// ---------------------------------------------------------------------
struct ToonParams
{
    // --- 基本カラー（サンプル済みで渡す） ---
    float4 mainTexColor;      
    float4 shadowTexColor;    //UseColorTexture=1 のとき使用

    // --- 影1（メインライト） ---
    float3 lightDir;          // 正規化前。内部で normalize
    float  threshold;
    float  softness;          // 影1専用
    float4 shadowColor;       // UseColorTexture=0 のとき影ソース生成に使用
    float  useColorTexture;

    // --- 影2 ---
    float  useShadow2;
    float3 shadow2LightDir;
    float  shadow2Threshold;
    float  shadow2Softness;   // 影2専用ぼかし（影1 softness とは独立）
    float4 shadow2Color;

    // --- リム ---
    float  useLimlight;
    float  limLightWidth;
    float4 rimColor;
    float  baseAlpha;         // 輪郭抽出の基準
    float  shiftedAlpha;      // 輪郭抽出のシフト側

    // --- サブライト ---
    float  useSublight;
    float3 subLightDir;
    float  subThreshold;
    float  subSoftness;
    float4 subColor;
    float  subColorIntensity;
};

// ---------------------------------------------------------------------
// 出力構造体
// - shadow1..sub : Pass 2 (ToonMRT) が SV_Target0..3 に振り分けて書き出す
//                  中身は「マスク × 色」（straight alpha）
// - composited   : Pass 1 (UniversalForward) がプレビューに使う全層合成後の色
//
// 「同じ計算 1 回で両方返す」= プレビューと書き出しの一致保証
// ---------------------------------------------------------------------
struct LayerResult
{
    float4 shadow1;    // SV_Target0 用: 影1マスク × 影1カラー
    float4 shadow2;    // SV_Target1 用: 影2マスク × 影2カラー
    float4 rim;        // SV_Target2 用: リムマスク × リムカラー
    float4 sub;        // SV_Target3 用: サブマスク × サブカラー
    float4 composited; // Pass1 プレビュー用: 全層合成後の最終カラー
};


// Linear ↔ sRGB 変換（piecewise sRGB 標準式・PSD と同じ精度）
float3 LinToSRGB(float3 c)
{
    c = saturate(c);
    float3 sLo = c * 12.92;
    float3 sHi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
    return lerp(sLo, sHi, step(0.0031308, c));
}
float3 SRGBToLin(float3 c)
{
    c = saturate(c);
    float3 lLo = c / 12.92;
    float3 lHi = pow((c + 0.055) / 1.055, 2.4);
    return lerp(lLo, lHi, step(0.04045, c));
}

// SoftLight ブレンド — Photoshop CS5+ / W3C CSS Compositing Level 1 準拠
// 式は PS CS5+ と同型 + PSD は sRGB 空間で演算するため、関数内部で Linear → sRGB
// に一旦持ち込んで計算 → Linear に戻す。これで PSD ソフトライト合成 = カンプ画像
// のピクセル一致を実現。
// 参照: https://www.w3.org/TR/compositing-1/
float3 SoftLightBlend(float3 base, float3 blend)
{
    // 入力を PSD 演算空間 (sRGB) に変換
    float3 baseS  = LinToSRGB(base);
    float3 blendS = LinToSRGB(blend);

    // Lo branch (blend <= 0.5)
    float3 lo = baseS - (1.0 - 2.0 * blendS) * baseS * (1.0 - baseS);

    // D(base): 暗部だけ cubic に切り替え（成分ごと分岐）
    float3 d;
    d.r = (baseS.r <= 0.25) ? ((16.0 * baseS.r - 12.0) * baseS.r + 4.0) * baseS.r : sqrt(baseS.r);
    d.g = (baseS.g <= 0.25) ? ((16.0 * baseS.g - 12.0) * baseS.g + 4.0) * baseS.g : sqrt(baseS.g);
    d.b = (baseS.b <= 0.25) ? ((16.0 * baseS.b - 12.0) * baseS.b + 4.0) * baseS.b : sqrt(baseS.b);

    // Hi branch (blend > 0.5)
    float3 hi = baseS + (2.0 * blendS - 1.0) * (d - baseS);

    float3 resultS = lerp(lo, hi, step(0.5, blendS));

    // Linear に戻す（呼び出し側は Linear 前提のパイプラインなので）
    return SRGBToLin(resultS);
}



// =====================================================================
// 層計算関数群
// =====================================================================

// ---------------------------------------------------------------------
// 影1 — smoothstep 型（メインライト）
// 返り値: MRT SV_Target0 用（straight alpha・生色）
//   .rgb = shadowColor 生 or shadowTexColor 生（useColorTexture 分岐）
//   .a   = mask × mainTexA
// out compositedRGB = プレビュー用（preview 側では SoftLight で合成する）
// PSD 推奨合成モード: shadowColor 時 = SoftLight / shadowTexColor 時 = 通常
// ---------------------------------------------------------------------
float4 ComputeShadow1(ToonParams p, float3 normal, float3 baseRGB, out float3 compositedRGB)
{
    float3 L = normalize(p.lightDir);
    float NdotL = dot(normal, L);
    float mask = smoothstep(p.threshold - p.softness, p.threshold, NdotL);
    float visibleMask = mask * p.mainTexColor.a;

    // preview 用: SoftLight or shadowTexture の合成後カラーで Lerp
    float3 previewShadowSource = lerp(
        SoftLightBlend(p.mainTexColor.rgb, p.shadowColor.rgb),
        p.shadowTexColor.rgb,
        p.useColorTexture);
    compositedRGB = lerp(baseRGB, previewShadowSource, mask);

    // MRT 用: 生色（straight alpha）
    float3 rawColor = lerp(p.shadowColor.rgb, p.shadowTexColor.rgb, p.useColorTexture);
    return float4(rawColor, visibleMask);
}
// ---------------------------------------------------------------------
// 影2 — 影1と同構造・独立ライト方向
// preview: baseRGB（影1適用後）に対して SoftLight → Lerp チェーン
// MRT: shadow2Color 生（影1非依存・完全独立）
// useShadow2 = 0 の場合は base をそのまま返す + マスク 0
// PSD 推奨合成モード: SoftLight
// ---------------------------------------------------------------------
float4 ComputeShadow2(ToonParams p, float3 normal, float3 baseRGB, out float3 compositedRGB)
{
    float3 L = normalize(p.shadow2LightDir);
    float NdotL = dot(normal, L);
    float mask = smoothstep(p.shadow2Threshold - p.shadow2Softness, p.shadow2Threshold, NdotL);
    mask *= p.useShadow2;
    float visibleMask = mask * p.mainTexColor.a;

    // preview 用: baseRGB (影1適用後) に対して SoftLight
    float3 previewShadow2Source = SoftLightBlend(baseRGB, p.shadow2Color.rgb);
    compositedRGB = lerp(baseRGB, previewShadow2Source, mask);

    // MRT 用: shadow2Color 生（straight alpha・影1非依存）
    return float4(p.shadow2Color.rgb, visibleMask);
}

// ---------------------------------------------------------------------
// リム — UVシフト輪郭 × 法線マスク
// UVシフト結果（baseAlpha / shiftedAlpha）は fragment 側で事前サンプルして
// ToonParams に詰めて渡す（HLSL の struct 内サンプル制限のため）
// useLimlight = 0 の場合はマスク 0
// addContribution: composited 加算用（mainTexA を含まない・生色 × mask）
// 返り値: MRT SV_Target2 用（straight alpha・生色）
// PSD 推奨合成モード: 加算
// ---------------------------------------------------------------------
float4 ComputeRim(ToonParams p, float3 normal, out float3 addContribution)
{
    float edgeMask = saturate(p.baseAlpha - p.shiftedAlpha);
    // --------------------------------------------------------------------------
    // 2Dイラスト用のノーマルマップは大部分が「正面向き」= (0,0,1) 付近なので、
    // NdotUp ベースの rimMask だと NdotUp ≈ 1 → rimFactor ≈ 0 → mask = 0 でリム全滅。
    // 元は 3D PoC（球体）向けの設計で、シルエット付近で法線が横向きになる前提だった。
    // Editor インポート済みの DXT5nm ノーマルなら法線が正しく復元されるため下記で合うが、
    // ランタイム LoadImage の生 RGB ノーマル運用と両立しないため撤去。
    // 3D 対応時に復活検討（閾値 0.48-0.51 は実測値）。
    // float NdotUp    = dot(normal, float3(0, 0, 1));   // 疑似視線
    // float rimFactor = 1.0 - NdotUp;
    // float rimMask   = smoothstep(0.48, 0.51, rimFactor);
    // float baseMask  = edgeMask * rimMask * p.useLimlight;
    // --------------------------------------------------------------------------
    float baseMask = edgeMask * p.useLimlight;
    float visibleMask = baseMask * p.mainTexColor.a;

    // preview 用: rimColor × baseMask（mainTexA なし）
    addContribution = p.rimColor.rgb * baseMask;

    // MRT 用: rimColor 生（straight alpha）
    return float4(p.rimColor.rgb, visibleMask);
}

// ---------------------------------------------------------------------
// サブライト — smoothstep 加算
// useSublight = 0 の場合はマスク 0
// addContribution: composited 加算用（mainTexA を含まない・intensity 込み）
// 返り値: MRT SV_Target3 用（straight alpha・生色・intensity は .a 側に寄せる）
// PSD 推奨合成モード: 加算
// ---------------------------------------------------------------------
float4 ComputeSubLight(ToonParams p, float3 normal, out float3 addContribution)
{
    float3 L = normalize(p.subLightDir);
    float NdotL = dot(normal, L);
    float mask = smoothstep(p.subThreshold - p.subSoftness, p.subThreshold, NdotL);
    mask *= p.useSublight;

    // preview 用: subColor × intensity × mask（mainTexA なし）
    addContribution = p.subColor.rgb * p.subColorIntensity * mask;

    // MRT 用: subColor 生（intensity は .a 側に寄せる）
    float visibleMask = mask * p.mainTexColor.a * p.subColorIntensity;
    return float4(p.subColor.rgb, visibleMask);
}

// =====================================================================
// トップレベル: 全層計算 + 合成
// 合成順: MainTex → 影1 Lerp → 影2 Lerp → サブ加算 → リム加算
// =====================================================================
LayerResult ComputeToonLayers(ToonParams p, float3 normal)
{
    LayerResult r;

    float3 composited = p.mainTexColor.rgb;

    // --- 影1 適用 ---
    r.shadow1 = ComputeShadow1(p, normal, composited, composited);

    // --- 影2 適用（useShadow2 は関数内でスイッチ） ---
    r.shadow2 = ComputeShadow2(p, normal, composited, composited);

    // --- サブライト加算 ---
    // r.sub.rgb は MRT 用 premultiplied なので、composited には addContribution を使う
    // （でないと Pass1 の Blend で alpha が二重適用される。リムも同様）
    // 加算は PSD 一致のため sRGB 空間で実行（影1 SoftLight と一貫）
    float3 subAdd;
    r.sub = ComputeSubLight(p, normal, subAdd);
    composited = SRGBToLin(LinToSRGB(composited) + LinToSRGB(subAdd));

    // --- リム加算（同じく sRGB 空間で加算） ---
    float3 rimAdd;
    r.rim = ComputeRim(p, normal, rimAdd);
    composited = SRGBToLin(LinToSRGB(composited) + LinToSRGB(rimAdd));

    // --- プレビュー最終カラー ---
    // composited.a は元絵の alpha を採用
    r.composited = float4(composited, p.mainTexColor.a);

    return r;
}

#endif // TOON_LAYERS_INCLUDED
