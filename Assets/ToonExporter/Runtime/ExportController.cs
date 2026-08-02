using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using ToonExporter.Core;
using ToonExporter.UI;
using USFB; // unity-standalone-file-browser (Sov3rain fork)

namespace ToonExporter.Runtime
{
    // =================================================================
    // ExportController — トゥーンレイヤー書き出しオペレーション統括
    //
    // USFB SaveFilePanel + .png 強制 + 二重起動ガード + try-finally 二段構え + トースト連携。
    // Core (ToonExportCore) の純関数を呼ぶ形で、書き出し本体ロジックは Core 側の責務。
    //
    // try-finally の「復帰対象」は isExporting フラグ + ImportLocked の 2 個。
    // =================================================================
    public class ExportController : MonoBehaviour
    {
        [Header("Renderer Data")]
        [Tooltip("URP の UniversalRendererData アセット。中の ToonMRTRendererFeature を自動検出する")]
        [SerializeField] private UniversalRendererData _rendererData;

        [Header("Preview Bridge")]
        [Tooltip("マテリアル反映用の PreviewController。未アサインなら同 GO からフォールバック取得。書き出し直前の Apply 保険に使う")]
        [SerializeField] private ToonPreviewController _previewController;

        [Header("Import Lock")]
        [Tooltip("書き出し中の画像差し替えをブロックする ImageImporter。未アサインなら同 GO からフォールバック取得")]
        [SerializeField] private ToonImageImporter _imageImporter;

        [Header("Export Settings")]
        [Tooltip("SaveFilePanel の初期ファイル名。拡張子 (.png) は付けない ← 付けると USFB が二重付加して baseName に残る")]
        [SerializeField] private string _defaultBaseName = "toon";

        // ------------------------------------------------------------------
        // 二重起動ガード。try-finally で確実に false に戻す
        // ------------------------------------------------------------------
        private bool _isExporting;

        /// <summary>書き出し中フラグ。UI 側でボタン enabled 制御に使う</summary>
        public bool IsExporting => _isExporting;

        /// <summary>書き出し状態が変わった時に発火。Panel がボタン enable/disable の再評価に使う</summary>
        public event Action ExportStateChanged;

        // Feature キャッシュ（毎フレーム走査回避）
        private ToonMRTRendererFeature _cachedFeature;

        // ------------------------------------------------------------------
        // 公開 API
        // ------------------------------------------------------------------

        /// <summary>
        /// UI の書き出しボタンから呼ばれるエントリポイント。
        /// state.targetCount に応じて DefaultCatalog の先頭 N 個 + comp を書き出す。
        /// state と RT 配列の切り出しはここで実施（サブセット決定は UI の役割じゃなく Controller の役割）。
        /// </summary>
        public void StartExport(ToonExporterState state)
        {
            if (_isExporting)
            {
                ToastManager.ShowOrLog("書き出し中に再呼び出しされました", ToastManager.ToastLevel.Warning);
                return;
            }

            if (state == null)
            {
                ToastManager.ShowOrLog("書き出し失敗: State が null", ToastManager.ToastLevel.Error);
                return;
            }

            // イラスト未ロードガード。ノーマルは任意（フラット法線でフォールバック成立）だが、
            // イラスト無しで焼くと真っ白 PNG になるだけで意味なし。Panel 経由じゃなく
            // 直接呼ばれる想定に備えて Coroutine 開始前にも防ぐ
            if (_imageImporter == null) _imageImporter = GetComponent<ToonImageImporter>();
            if (_imageImporter == null || !_imageImporter.HasIllustration)
            {
                ToastManager.ShowOrLog(
                    "書き出し失敗: イラストが未ロードです。「イラストを読み込み」ボタンから画像を選択してください",
                    ToastManager.ToastLevel.Warning);
                return;
            }

            var feature = GetFeature();
            if (feature == null)
            {
                ToastManager.ShowOrLog(
                    "書き出し失敗: ToonMRTRendererFeature が見つからない。Inspector で RendererData をアサインして",
                    ToastManager.ToastLevel.Error);
                return;
            }

            var allRTs = feature.LayerRTs;
            if (allRTs == null || allRTs.Length == 0 || allRTs[0] == null)
            {
                ToastManager.ShowOrLog(
                    "書き出し失敗: RT が未確保。カメラで 1 フレーム以上描画してから実行して",
                    ToastManager.ToastLevel.Error);
                return;
            }

            // USFB SaveFilePanel でファイル名 + 保存先ディレクトリを取得。
            // 「1 個の代表ファイル」を選ぶ UX で複数 PNG のディレクトリ + baseName を確定する。
            //
            // 【罠】initialName に拡張子込みを渡すと、拡張子フィルタ ("png") 併用時に
            // Windows ダイアログが「toon.png」を丸ごと名前部分と認識し、保存時にフィルタが
            // 再度 ".png" を付加して "toon.png.png" が返る。GetFileNameWithoutExtension は
            // 末尾1拡張子しか剥がさないので baseName に ".png" が残り、各出力ファイルが
            // "toon.png_shadow1_softlight.png" になる。initialName は拡張子なしで渡すのが正。
            string path = StandaloneFileBrowser.SaveFilePanel(
                "トゥーンレイヤーを書き出し", "", _defaultBaseName, "png");

            if (string.IsNullOrEmpty(path))
            {
                // キャンセルは静かに戻る（トースト不要）
                return;
            }

            // USFB は拡張子フィルタを指定してもユーザーが拡張子省略するとそのまま返す。
            // EncodeToPNG で書く以上 .png 強制が正解（拡張子なしファイルが生まれる事故防止）。
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                path += ".png";
            }

            string dir = Path.GetDirectoryName(path);
            string baseName = Path.GetFileNameWithoutExtension(path);

            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName))
            {
                ToastManager.ShowOrLog(
                    $"書き出し失敗: 保存先パスが不正 ({path})",
                    ToastManager.ToastLevel.Error);
                return;
            }

            // targetCount → RT + Slot サブセット構築
            //   3 本 → shadow1, shadow2, rim, comp = 4 PNG
            //   4 本 → shadow1, shadow2, rim, sub, comp = 5 PNG
            // comp（DefaultCatalog[4]）は本数に含まれず常時最後に付ける（絵描き復元用のカンプ画像）
            if (!TryBuildSubsets(state.targetCount, allRTs, out var rtsSubset, out var slotsSubset, out var errorMsg))
            {
                ToastManager.ShowOrLog($"書き出し失敗: {errorMsg}", ToastManager.ToastLevel.Error);
                return;
            }

            // state を coroutine に渡す。early return 経路で Apply が走るのを避けるため、
            // "焼く直前の保険" として実際に焼く直前＝コルーチン内で叩く
            StartCoroutine(ExportCoroutine(rtsSubset, slotsSubset, dir, baseName, state));
        }

        // ------------------------------------------------------------------
        // 書き出しコルーチン（try-finally 二段構え）
        // ------------------------------------------------------------------

        private IEnumerator ExportCoroutine(
            RenderTexture[] rts, ToonLayerSlot[] slots, string dir, string baseName, ToonExporterState state)
        {
            _isExporting = true;
            ExportStateChanged?.Invoke();

            // 書き出し中の画像差し替えをブロック。ImageImporter が LoadImage 内で override を書き換えると、
            // WaitForEndOfFrame 中に RT が Release→再確保されて焼く対象が一瞬 null / 別サイズになる事故を防ぐ
            if (_imageImporter != null) _imageImporter.ImportLocked = true;

            ToastManager.ShowOrLog("書き出し中…", ToastManager.ToastLevel.Info);

            // 焼く直前に UI 値をマテリアルへ強制反映。ライブ購読の初期化順序やイベント取りこぼしの保険。冪等
            if (_previewController == null) _previewController = GetComponent<ToonPreviewController>();
            _previewController?.ApplyToMaterial(state);

            // フレーム描画完了を待つ（無いと真っ黒 PNG になる）。
            // MRT の SetRenderAttachment は AfterRenderingOpaques で走るので、
            // 現フレの描画完了を待ってから ReadPixels しないと空 RT を焼く。
            yield return new WaitForEndOfFrame();

            string[] paths = null;
            try
            {
                paths = ToonExportCore.ExportLayers(rts, slots, dir, baseName);

                if (paths != null && paths.Length > 0)
                {
                    // フルパスをトーストに出すと横幅切れするので dir だけ表示。個別ファイル名は Core 側の Debug.Log に出てる
                    ToastManager.ShowOrLog(
                        $"{paths.Length} 枚の PNG を書き出しました: {dir}",
                        ToastManager.ToastLevel.Success);
                }
                else
                {
                    ToastManager.ShowOrLog(
                        "書き出し失敗: ExportLayers が 0 件を返した（Console のエラーログを確認）",
                        ToastManager.ToastLevel.Error);
                }
            }
            catch (Exception e)
            {
                // フルスタックトレースは Console、短文だけトーストに出す
                Debug.LogException(e);
                ToastManager.ShowOrLog($"書き出し失敗: {e.Message}", ToastManager.ToastLevel.Error);
            }
            finally
            {
                // 状態復帰（ここだけは絶対通す）。例外経由でも確実に画像ロック解除される
                _isExporting = false;
                if (_imageImporter != null) _imageImporter.ImportLocked = false;
                ExportStateChanged?.Invoke();
            }
        }

        // ------------------------------------------------------------------
        // サブセット構築 — targetCount に応じて RT + Slot の N 個を切り出し + comp を末尾に付ける
        // ------------------------------------------------------------------

        private static bool TryBuildSubsets(
            int targetCount,
            RenderTexture[] allRTs,
            out RenderTexture[] rtsSubset,
            out ToonLayerSlot[] slotsSubset,
            out string errorMsg)
        {
            rtsSubset = null;
            slotsSubset = null;
            errorMsg = null;

            // targetCount の妥当性チェック
            if (targetCount != 3 && targetCount != 4)
            {
                errorMsg = $"targetCount が不正 ({targetCount})。3 か 4 を期待";
                return false;
            }

            var catalog = ToonLayerSlot.DefaultCatalog;

            // DefaultCatalog は SV_Target0..4 の 5 スロット。末尾がカンプ画像固定
            const int compIndex = 4;

            if (catalog.Length <= compIndex)
            {
                errorMsg = $"DefaultCatalog の長さ不足 ({catalog.Length})。5 スロット期待";
                return false;
            }

            if (allRTs.Length <= compIndex)
            {
                errorMsg = $"RT 配列の長さ不足 ({allRTs.Length})。5 本期待";
                return false;
            }

            // subset は N（3 or 4）+ comp 1 個 = N+1 個
            int subsetLen = targetCount + 1;
            rtsSubset = new RenderTexture[subsetLen];
            slotsSubset = new ToonLayerSlot[subsetLen];

            // 先頭 N 個: DefaultCatalog[0..N-1]（shadow1, shadow2, rim, [sub]）
            for (int i = 0; i < targetCount; i++)
            {
                rtsSubset[i] = allRTs[i];
                slotsSubset[i] = catalog[i];
            }

            // 末尾: comp（DefaultCatalog[4]）
            rtsSubset[targetCount] = allRTs[compIndex];
            slotsSubset[targetCount] = catalog[compIndex];

            return true;
        }

        // ------------------------------------------------------------------
        // Feature 取得
        // ------------------------------------------------------------------

        private ToonMRTRendererFeature GetFeature()
        {
            if (_cachedFeature != null) return _cachedFeature;

            if (_rendererData == null)
            {
                Debug.LogError("[ExportController] RendererData が未設定。Inspector で UniversalRendererData をアサインして");
                return null;
            }

            _cachedFeature = ToonMRTRendererFeature.FindIn(_rendererData);

            if (_cachedFeature == null)
            {
                Debug.LogError("[ExportController] ToonMRTRendererFeature が RendererData に追加されていません");
            }
            return _cachedFeature;
        }
    }
}
