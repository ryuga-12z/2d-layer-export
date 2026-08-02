using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

/// <summary>
/// MRT 5本（各層4本 + カンプ1本）にトゥーンレイヤーを振り分けて描画する RendererFeature。
/// RenderGraph正攻法（AddRasterRenderPass + SetRenderAttachment）で組んでいる。
/// Compatibility Mode は Unity 6.3 以降で削除済みなので選択肢なし。
/// SV_Target4 = カンプ画像（全層合成後・絵描き復元手引き）
/// </summary>
public class ToonMRTRendererFeature : ScriptableRendererFeature
{
    [Header("MRT Settings")]
    [Tooltip("ToonMRT LightMode タグを持つシェーダーが貼られたオブジェクトだけ描画対象になる")]
    public LayerMask targetLayerMask = -1;

    [Tooltip("RT の解像度。0 だとカメラ解像度をそのまま使う")]
    public int overrideWidth = 0;
    public int overrideHeight = 0;

    // Exporter がここから RT を引っ張る
    public RenderTexture Shadow1RT { get; private set; }
    public RenderTexture Shadow2RT { get; private set; }
    public RenderTexture RimRT { get; private set; }
    public RenderTexture SubRT { get; private set; }
    // SV_Target4 = カンプ画像
    public RenderTexture CompRT { get; private set; }

    /// <summary>
    /// 全スロットの RT を配列で返す。SV_Target0..4 の順。
    /// 呼び出し頻度は低い（書き出し時のみ）ので毎回 new で問題ない。
    /// </summary>
    public RenderTexture[] LayerRTs => new RenderTexture[]
    {
        Shadow1RT, Shadow2RT, RimRT, SubRT, CompRT
    };

    // RenderGraph にインポートするための RTHandle（毎フレーム Alloc しないようキャッシュ）
    private RTHandle _shadow1Handle;
    private RTHandle _shadow2Handle;
    private RTHandle _rimHandle;
    private RTHandle _subHandle;
    private RTHandle _compHandle;

    private ToonMRTRenderPass _pass;

    public override void Create()
    {
        _pass = new ToonMRTRenderPass(this);
        _pass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    /// <summary>
    /// UniversalRendererData から ToonMRTRendererFeature を取り出す共通ヘルパー。
    /// 見つからない場合は null を返す（呼び出し側でエラーログ／トーストを出す）。
    /// </summary>
    public static ToonMRTRendererFeature FindIn(UniversalRendererData rendererData)
    {
        if (rendererData == null) return null;
        foreach (var f in rendererData.rendererFeatures)
        {
            if (f is ToonMRTRendererFeature mrtFeature) return mrtFeature;
        }
        return null;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_pass);
    }

    /// <summary>
    /// RT を確保 or リサイズ。カメラ解像度が変わったら再生成。
    /// R8G8B8A8_UNorm + sRGB=false で統一（R8G8B8A8_SRGB は差し戻しバグあり）。
    /// </summary>
    public void EnsureRenderTextures(int width, int height)
    {
        if (Shadow1RT != null && Shadow1RT.width == width && Shadow1RT.height == height)
            return;

        ReleaseRenderTextures();

        Shadow1RT = CreateRT(width, height, "ToonMRT_Shadow1");
        Shadow2RT = CreateRT(width, height, "ToonMRT_Shadow2");
        RimRT    = CreateRT(width, height, "ToonMRT_Rim");
        SubRT    = CreateRT(width, height, "ToonMRT_Sub");
        CompRT   = CreateRT(width, height, "ToonMRT_Comp");

        // RTHandle をキャッシュ（ImportTexture 用）
        _shadow1Handle = RTHandles.Alloc(Shadow1RT);
        _shadow2Handle = RTHandles.Alloc(Shadow2RT);
        _rimHandle     = RTHandles.Alloc(RimRT);
        _subHandle     = RTHandles.Alloc(SubRT);
        _compHandle    = RTHandles.Alloc(CompRT);
    }

    /// <summary>
    /// キャッシュ済み RTHandle を返す。RecordRenderGraph 内で使用。
    /// </summary>
    public (RTHandle shadow1, RTHandle shadow2, RTHandle rim, RTHandle sub, RTHandle comp) GetRTHandles()
    {
        return (_shadow1Handle, _shadow2Handle, _rimHandle, _subHandle, _compHandle);
    }

    public void ReleaseRenderTextures()
    {
        ReleaseRTHandle(ref _shadow1Handle);
        ReleaseRTHandle(ref _shadow2Handle);
        ReleaseRTHandle(ref _rimHandle);
        ReleaseRTHandle(ref _subHandle);
        ReleaseRTHandle(ref _compHandle);

        Shadow1RT = ReleaseRT(Shadow1RT);
        Shadow2RT = ReleaseRT(Shadow2RT);
        RimRT    = ReleaseRT(RimRT);
        SubRT    = ReleaseRT(SubRT);
        CompRT   = ReleaseRT(CompRT);
    }

    private static RenderTexture CreateRT(int w, int h, string name)
    {
        var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        rt.name = name;
        rt.filterMode = FilterMode.Point;
        rt.Create();
        return rt;
    }

    // プロパティは ref 渡し不可なので、値受け取り→nullを返す形にして代入で回す
    private static RenderTexture ReleaseRT(RenderTexture rt)
    {
        if (rt != null)
        {
            rt.Release();
            if (Application.isPlaying)
                Destroy(rt);
            else
                DestroyImmediate(rt);
        }
        return null;
    }

    private static void ReleaseRTHandle(ref RTHandle handle)
    {
        if (handle != null)
        {
            handle.Release();
            handle = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        ReleaseRenderTextures();
    }

    // ---------------------------------------------------------------
    // RenderPass
    // ---------------------------------------------------------------
    private class ToonMRTRenderPass : ScriptableRenderPass
    {
        private readonly ToonMRTRendererFeature _feature;
        private readonly ShaderTagId _shaderTagId = new ShaderTagId("ToonMRT");

        private class PassData
        {
            public RendererListHandle rendererListHandle;
        }

        public ToonMRTRenderPass(ToonMRTRendererFeature feature)
        {
            _feature = feature;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var cameraData = frameData.Get<UniversalCameraData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var lightData = frameData.Get<UniversalLightData>();

            int width  = _feature.overrideWidth  > 0 ? _feature.overrideWidth  : cameraData.cameraTargetDescriptor.width;
            int height = _feature.overrideHeight > 0 ? _feature.overrideHeight : cameraData.cameraTargetDescriptor.height;

            // RT 確保（解像度変更時のみ再生成される）
            _feature.EnsureRenderTextures(width, height);

            // キャッシュ済み RTHandle から RenderGraph 用 TextureHandle に変換
            var handles = _feature.GetRTHandles();
            var shadow1Tex = renderGraph.ImportTexture(handles.shadow1);
            var shadow2Tex = renderGraph.ImportTexture(handles.shadow2);
            var rimTex     = renderGraph.ImportTexture(handles.rim);
            var subTex     = renderGraph.ImportTexture(handles.sub);
            var compTex    = renderGraph.ImportTexture(handles.comp);

            // Depth はフレームごとの一時バッファでOK
            var depthDesc = new TextureDesc(width, height)
            {
                depthBufferBits = DepthBits.Depth32,
                name = "ToonMRT_Depth"
            };
            var depthHandle = renderGraph.CreateTexture(depthDesc);

            // ToonMRT LightMode タグでフィルタした RendererList
            var drawSettings = RenderingUtils.CreateDrawingSettings(
                _shaderTagId,
                renderingData,
                cameraData,
                lightData,
                SortingCriteria.CommonOpaque
            );
            var filterSettings = new FilteringSettings(RenderQueueRange.all, _feature.targetLayerMask);
            var rendererListHandle = renderGraph.CreateRendererList(
                new RendererListParams(renderingData.cullResults, drawSettings, filterSettings)
            );

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("ToonMRT_Pass", out var passData))
            {
                passData.rendererListHandle = rendererListHandle;

                // MRT 5本セット（層4本 + カンプ1本）
                builder.SetRenderAttachment(shadow1Tex, 0, AccessFlags.Write);
                builder.SetRenderAttachment(shadow2Tex, 1, AccessFlags.Write);
                builder.SetRenderAttachment(rimTex,     2, AccessFlags.Write);
                builder.SetRenderAttachment(subTex,     3, AccessFlags.Write);
                builder.SetRenderAttachment(compTex,    4, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depthHandle, AccessFlags.Write);

                builder.UseRendererList(rendererListHandle);

                // 外部RTへ書き出すパスなので RenderGraph にカリングさせない
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                {
                    // Clear (0,0,0,0)：背景透過のため全ターゲットを透明黒でクリア
                    ctx.cmd.ClearRenderTarget(RTClearFlags.All, Color.clear, 1.0f, 0);
                    ctx.cmd.DrawRendererList(data.rendererListHandle);
                });
            }
        }
    }
}
