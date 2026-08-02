#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering.Universal;
using System.IO;

/// <summary>
/// PoC用のエクスポートウィンドウ。ボタン1個で MRT 4層 + カンプ画像 = 5枚の PNG を書き出す。
/// 書き出しロジックは ToonExportCore に委譲。
/// Editor専用の責務（AssetDatabase.Refresh, ダイアログ表示, RendererFeature検索）だけここに残す。
/// </summary>
public class ToonExporterWindow : EditorWindow
{
    private static string OutputFullPath => Path.Combine(Application.dataPath, "Output");
    private const string OutputRelative = "Assets/Output";

    [MenuItem("Tools/Toon Exporter/Export MRT Layers")]
    public static void ShowWindow()
    {
        GetWindow<ToonExporterWindow>("Toon MRT Exporter");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Toon MRT Layer Exporter (PoC)", EditorStyles.boldLabel);
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "MRT 4層（Shadow1 / Shadow2 / Rim / Sub）+ カンプ画像（Comp）の計5枚をPNGに書き出す。\n" +
            "URP Renderer Data に ToonMRTRendererFeature が登録されている必要あり。",
            MessageType.Info
        );
        EditorGUILayout.Space();

        if (GUILayout.Button("Export Layers", GUILayout.Height(40)))
        {
            ExportLayers();
        }
    }

    private void ExportLayers()
    {
        var feature = FindMRTFeature();
        if (feature == null)
        {
            EditorUtility.DisplayDialog(
                "ToonMRT Error",
                "ToonMRTRendererFeature が見つからない。\n" +
                "URP Renderer Data に追加されてるか確認して。",
                "OK"
            );
            return;
        }

        // RT は描画1フレ目で初めて確保される。Play 前 or 未描画状態だと null
        if (feature.Shadow1RT == null)
        {
            EditorUtility.DisplayDialog(
                "ToonMRT Error",
                "RT がまだ確保されてない。\n" +
                "Game ビューで1フレーム以上再生してから実行して。",
                "OK"
            );
            return;
        }

        var paths = ToonExportCore.ExportLayers(feature.LayerRTs, ToonLayerSlot.DefaultCatalog, OutputFullPath, "toon");

        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog(
            "ToonMRT Export",
            $"{paths.Length} 枚の PNG を書き出した。\n出力先: {OutputRelative}/",
            "OK"
        );
    }

    /// <summary>
    /// アクティブな URP Renderer から ToonMRTRendererFeature を探す。
    /// SerializedObject 経由（Editor 専用）。ランタイム版は ToonExporterPanel の書き出しボタン経由。
    /// </summary>
    private static ToonMRTRendererFeature FindMRTFeature()
    {
        var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (pipeline == null)
            return null;

        // UniversalRenderPipelineAsset は m_RendererDataList を public API で公開してないので SerializedObject 経由で読む
        var so = new SerializedObject(pipeline);
        var rendererListProp = so.FindProperty("m_RendererDataList");

        if (rendererListProp == null || !rendererListProp.isArray)
            return null;

        for (int i = 0; i < rendererListProp.arraySize; i++)
        {
            var element = rendererListProp.GetArrayElementAtIndex(i);
            var rendererData = element.objectReferenceValue as UniversalRendererData;
            if (rendererData == null)
                continue;

            foreach (var feature in rendererData.rendererFeatures)
            {
                if (feature is ToonMRTRendererFeature mrtFeature)
                    return mrtFeature;
            }
        }

        return null;
    }
}
#endif
