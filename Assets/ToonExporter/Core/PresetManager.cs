using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ToonExporter.Core
{
    // =================================================================
    // PresetManager — トゥーンレイヤー書き出しツール Preset 永続化層
    //
    // 保存先は persistentDataPath/ToonPresets/ にサブフォルダ分離（他ツールと同居時の衝突回避）。
    //
    // Core は UIElements/USFB を参照しない鉄則があるので、通知は
    // static event OnNotification で UI 側に橋渡し。購読者ゼロ時は
    // Debug.Log にフォールバック（無音死しない）。
    // =================================================================
    public static class PresetManager
    {
        private const string PresetFolderName = "ToonPresets";
        private const string FileExtension = ".json";

        // ファイル名に使えない文字は .NET 標準 API から取得。
        // 独自定義だと制御文字（0x00-0x1F）等が漏れるので Path.GetInvalidFileNameChars() に任せる。
        private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

        // ------------------------------------------------------------------
        // 通知フック — UI 層への疎結合連絡
        // ------------------------------------------------------------------

        /// <summary>プリセット操作の通知レベル（ToastLevel と 1:1 対応）</summary>
        public enum NotifyLevel
        {
            Info,
            Success,
            Warning,
            Error,
        }

        /// <summary>
        /// プリセット操作の結果通知。UI 側でトーストや Debug.Log に橋渡しする。
        /// 購読者ゼロなら Debug.Log にフォールバック（無音死回避）。
        /// </summary>
        public static event Action<string, NotifyLevel> OnNotification;

        /// <summary>
        /// 通知の発火ヘルパー。購読者ゼロなら Debug.Log にフォールバック。
        /// Core 側はここだけ触れば UI 依存なしで通知できる。
        /// </summary>
        private static void Notify(string message, NotifyLevel level)
        {
            if (OnNotification != null)
            {
                OnNotification.Invoke(message, level);
                return;
            }

            // 購読者ゼロ時のフォールバック（起動前の Initialize 等でも落とさない）
            switch (level)
            {
                case NotifyLevel.Error:
                    Debug.LogError($"[PresetManager] {message}");
                    break;
                case NotifyLevel.Warning:
                    Debug.LogWarning($"[PresetManager] {message}");
                    break;
                default:
                    Debug.Log($"[PresetManager] {message}");
                    break;
            }
        }

        // ------------------------------------------------------------------
        // パス解決
        // ------------------------------------------------------------------

        /// <summary>ユーザー Preset の保存先ディレクトリ</summary>
        public static string UserPresetDir =>
            Path.Combine(Application.persistentDataPath, PresetFolderName);

        /// <summary>デフォルト Preset の同梱先（読み取り専用）</summary>
        public static string StreamingPresetDir =>
            Path.Combine(Application.streamingAssetsPath, PresetFolderName);

        /// <summary>指定プリセット名のフルパスを返す</summary>
        private static string GetPresetPath(string presetName) =>
            Path.Combine(UserPresetDir, presetName + FileExtension);

        // ------------------------------------------------------------------
        // 初期化（起動時に 1 回呼ぶ想定）
        // ------------------------------------------------------------------

        /// <summary>
        /// 保存先ディレクトリ作成 + デフォルト Preset の初回コピー。
        /// MonoBehaviour.Awake 等から呼ぶ。
        /// </summary>
        public static void Initialize()
        {
            if (!EnsurePresetDirectory())
                return; // ディレクトリ作成失敗時はデフォルト Preset コピーもスキップ

            CopyDefaultPresetsIfNeeded();
        }

        /// <summary>
        /// Preset ディレクトリの存在を保証する。
        /// ディスクフル・パーミッション不足等で作成失敗した場合は false を返す。
        /// 配布アプリではユーザー環境で何が起きるかわからないので例外を握りつぶさず通知する。
        /// </summary>
        private static bool EnsurePresetDirectory()
        {
            try
            {
                if (!Directory.Exists(UserPresetDir))
                {
                    Directory.CreateDirectory(UserPresetDir);
                    Debug.Log($"[PresetManager] Preset ディレクトリ作成: {UserPresetDir}");
                }
                return true;
            }
            catch (Exception e)
            {
                // ディスクフル・パーミッション不足・パス長超過等
                Notify($"Preset フォルダの作成に失敗: {e.Message}", NotifyLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// StreamingAssets/ToonPresets/ の JSON をユーザー Preset フォルダにコピー。
        /// 既存ファイルは上書きしない（ユーザーが編集した可能性があるため）。
        /// </summary>
        private static void CopyDefaultPresetsIfNeeded()
        {
            string srcDir = StreamingPresetDir;
            if (!Directory.Exists(srcDir))
            {
                // StreamingAssets に ToonPresets フォルダがない = デフォルト Preset 未同梱
                Debug.Log("[PresetManager] StreamingAssets/ToonPresets/ が見つからない。デフォルト Preset スキップ");
                return;
            }

            string[] files = Directory.GetFiles(srcDir, "*" + FileExtension);
            foreach (string srcPath in files)
            {
                string fileName = Path.GetFileName(srcPath);
                string destPath = Path.Combine(UserPresetDir, fileName);

                if (File.Exists(destPath))
                    continue; // 上書きしない

                try
                {
                    File.Copy(srcPath, destPath);
                    Debug.Log($"[PresetManager] デフォルト Preset コピー: {fileName}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[PresetManager] デフォルト Preset コピー失敗: {fileName} - {e.Message}");
                }
            }
        }

        // ------------------------------------------------------------------
        // 保存
        // ------------------------------------------------------------------

        /// <summary>
        /// State を JSON ファイルとして保存する。
        /// 成功時 true、失敗時 false（通知済み）。
        /// </summary>
        public static bool Save(string presetName, ToonExporterState state)
        {
            if (string.IsNullOrWhiteSpace(presetName))
            {
                Notify("プリセット名が空", NotifyLevel.Warning);
                return false;
            }

            if (state == null)
            {
                Notify("保存する State が null", NotifyLevel.Warning);
                return false;
            }

            if (!ValidateFileName(presetName))
                return false;

            // ユーザーが persistentDataPath 配下を手動削除した場合の保険
            if (!EnsurePresetDirectory())
                return false;

            try
            {
                string json = JsonUtility.ToJson(state, prettyPrint: true);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                File.WriteAllBytes(GetPresetPath(presetName), bytes);

                Notify($"Preset 保存: {presetName}", NotifyLevel.Success);
                return true;
            }
            catch (Exception e)
            {
                Notify($"Preset 保存失敗: {e.Message}", NotifyLevel.Error);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 読込
        // ------------------------------------------------------------------

        /// <summary>
        /// JSON ファイルから State を読み込んで返す。
        /// UI 反映は呼び出し側の責務（InitFromState + StateChanged 発火）。
        /// 失敗時は null を返す（通知済み）。
        /// </summary>
        public static ToonExporterState Load(string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName))
            {
                Notify("プリセット名が空", NotifyLevel.Warning);
                return null;
            }

            string path = GetPresetPath(presetName);
            if (!File.Exists(path))
            {
                Notify($"Preset が見つからない: {presetName}", NotifyLevel.Error);
                return null;
            }

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var state = JsonUtility.FromJson<ToonExporterState>(json);

                // バージョンチェック。
                // JsonUtility は未知フィールドをスキップ・不足は初期値で埋めるので
                // version 不一致でも読めはする。警告だけ出して続行。
                if (state != null && state.version != ToonExporterState.LatestVersion)
                {
                    Notify(
                        $"Preset バージョン不一致 (v{state.version} / 現在 v{ToonExporterState.LatestVersion})。一部初期値で読み込み",
                        NotifyLevel.Warning);
                }

                Notify($"Preset 読込: {presetName}", NotifyLevel.Success);
                return state;
            }
            catch (Exception e)
            {
                Notify($"Preset 読込失敗: {e.Message}", NotifyLevel.Error);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // 削除
        // ------------------------------------------------------------------

        /// <summary>
        /// Preset ファイルを削除する。
        /// 確認ダイアログは UI 側の責務（ここでは聞かない）。
        /// </summary>
        public static bool Delete(string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName))
                return false;

            string path = GetPresetPath(presetName);
            if (!File.Exists(path))
            {
                Notify($"削除対象が見つからない: {presetName}", NotifyLevel.Warning);
                return false;
            }

            try
            {
                File.Delete(path);
                Notify($"Preset 削除: {presetName}", NotifyLevel.Success);
                return true;
            }
            catch (Exception e)
            {
                Notify($"Preset 削除失敗: {e.Message}", NotifyLevel.Error);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 一覧取得
        // ------------------------------------------------------------------

        /// <summary>
        /// Preset フォルダ内の JSON ファイル名（拡張子なし）をリストで返す。
        /// 更新日時の昇順（古い→新しい）でソートして返す。
        /// 初期プリセットは StreamingAssets コピー時刻で自然に先頭、
        /// ユーザー追加分が末尾に来る＝最近作ったものがすぐ見える。
        /// 空フォルダなら空リスト。
        /// </summary>
        public static List<string> GetPresetNames()
        {
            var names = new List<string>();

            if (!Directory.Exists(UserPresetDir))
                return names;

            string[] files = Directory.GetFiles(UserPresetDir, "*" + FileExtension);

            // 更新日時の昇順ソート（古い順→新しい順）。
            // File.GetLastWriteTime は UTC 変換不要。ローカルマシン内の相対比較なので
            // タイムゾーン差異は問題にならない（同一マシン上の同一ディレクトリ内比較）。
            Array.Sort(files, (a, b) =>
                File.GetLastWriteTime(a).CompareTo(File.GetLastWriteTime(b)));

            foreach (string file in files)
                names.Add(Path.GetFileNameWithoutExtension(file));

            return names;
        }

        /// <summary>指定名の Preset が存在するか</summary>
        public static bool Exists(string presetName) =>
            !string.IsNullOrWhiteSpace(presetName) && File.Exists(GetPresetPath(presetName));

        // ------------------------------------------------------------------
        // バリデーション
        // ------------------------------------------------------------------

        /// <summary>
        /// ファイル名に使えない文字が含まれてないかチェック。
        /// 不正なら通知して false。
        /// </summary>
        private static bool ValidateFileName(string name)
        {
            foreach (char c in InvalidChars)
            {
                if (name.IndexOf(c) >= 0)
                {
                    // 制御文字（0x00-0x1F）はトーストで見えないので 16 進表記にする
                    string display = char.IsControl(c) ? $"0x{(int)c:X2}" : $"'{c}'";
                    Notify(
                        $"プリセット名に使えない文字 {display} が含まれてる",
                        NotifyLevel.Warning);
                    return false;
                }
            }

            // 先頭・末尾の空白やドットも Windows は嫌がる。
            // TrimEnd('.') のみ = 中間のドットは許可（例: "my.preset" → OK）。
            // ファイル名に拡張子的なドットを含めたいケースを考慮した仕様。
            string trimmed = name.Trim().TrimEnd('.');
            if (trimmed != name || trimmed.Length == 0)
            {
                Notify(
                    "プリセット名の先頭/末尾に空白やドットは使えない",
                    NotifyLevel.Warning);
                return false;
            }

            return true;
        }
    }
}
