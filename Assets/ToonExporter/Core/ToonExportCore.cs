using UnityEngine;
using System;
using System.IO;

/// <summary>
/// MRT N本 → PNG 書き出しの純関数コア。
/// Editor に一切依存しない。ビルドした Player でもそのまま動く。
/// 具象 RendererFeature への依存を持たず、RT配列 + スロット配列を受け取る疎結合設計。
/// </summary>
public static class ToonExportCore
{
    /// <summary>
    /// RT 配列を sRGB Blit → ReadPixels → PNG 保存。
    /// Editor API を一切使わない。AssetDatabase.Refresh 等は呼び出し側の責務。
    /// rts と slots は同じ長さで、rts[i] を slots[i].suffix 付きファイル名で保存する。
    /// </summary>
    /// <param name="rts">書き出し対象の RT 配列（RendererFeature 等から取得済みのもの）</param>
    /// <param name="slots">各 RT に対応するスロット定義（suffix 等）</param>
    /// <param name="outputDirectory">PNG の書き出し先ディレクトリ（絶対パス）</param>
    /// <param name="baseName">ファイル名のプレフィクス（例: "toon" → "toon_shadow1_softlight.png"）</param>
    /// <returns>書き出したファイルパス配列。失敗時は空配列</returns>
    public static string[] ExportLayers(
        RenderTexture[] rts,
        ToonLayerSlot[] slots,
        string outputDirectory,
        string baseName = "toon")
    {
        if (rts == null || slots == null || rts.Length != slots.Length)
        {
            Debug.LogError(
                $"[ToonExporter] RT配列とスロット配列が不正 (rts={rts?.Length}, slots={slots?.Length})");
            return Array.Empty<string>();
        }

        // RT は描画1フレ目で初めて確保される。Play 前 or 未描画状態だと null
        if (rts.Length == 0 || rts[0] == null)
        {
            Debug.LogError("[ToonExporter] RT が未確保。カメラで1フレーム以上描画してから実行して");
            return Array.Empty<string>();
        }

        try
        {
            if (!Directory.Exists(outputDirectory))
                Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ToonExporter] 出力ディレクトリ作成に失敗: {outputDirectory}\n{e}");
            return Array.Empty<string>();
        }

        var exportedPaths = new string[rts.Length];
        int exportedCount = 0;

        for (int i = 0; i < rts.Length; i++)
        {
            // 個別 RT の null は警告出してスキップ（部分書き出しを許容）
            if (rts[i] == null)
            {
                Debug.LogWarning($"[ToonExporter] RT[{i}] ({slots[i].suffix}) が null、スキップ");
                continue;
            }

            var fileName = $"{baseName}_{slots[i].suffix}.png";
            var filePath = Path.Combine(outputDirectory, fileName);

            try
            {
                SaveRTToPNG(rts[i], filePath);
                exportedPaths[exportedCount] = filePath;
                exportedCount++;
                Debug.Log($"[ToonExporter] Exported: {filePath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ToonExporter] PNG書き出し失敗 ({slots[i].suffix}): {e}");
            }
        }

        if (exportedCount == rts.Length)
            return exportedPaths;

        if (exportedCount == 0)
            return Array.Empty<string>();

        var trimmed = new string[exportedCount];
        Array.Copy(exportedPaths, trimmed, exportedCount);
        return trimmed;
    }

    /// <summary>
    /// Linear RT → sRGB 変換 RT に Blit → ReadPixels → PNG 保存。
    /// Linear プロジェクトで ReadPixels を直叩きすると暗くなる問題の対策（研究ドキュ §R2）。
    /// </summary>
    private static void SaveRTToPNG(RenderTexture srcRT, string filePath)
    {
        int w = srcRT.width;
        int h = srcRT.height;

        // sRGB 一時 RT へ Blit することで Linear→sRGB 変換がかかる
        var srgbRT = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

        try
        {
            Graphics.Blit(srcRT, srgbRT);

            var prevActive = RenderTexture.active;
            RenderTexture.active = srgbRT;

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();

            RenderTexture.active = prevActive;

            var pngBytes = tex.EncodeToPNG();
            File.WriteAllBytes(filePath, pngBytes);

            SafeDestroy(tex);
        }
        finally
        {
            RenderTexture.ReleaseTemporary(srgbRT);
        }
    }

    /// <summary>
    /// Application.isPlaying で Destroy / DestroyImmediate を分岐。
    /// ビルド時に DestroyImmediate を呼ぶと警告が出る問題の回避。
    /// </summary>
    private static void SafeDestroy(UnityEngine.Object obj)
    {
        if (obj == null) return;

        if (Application.isPlaying)
            UnityEngine.Object.Destroy(obj);
        else
            UnityEngine.Object.DestroyImmediate(obj);
    }
}
