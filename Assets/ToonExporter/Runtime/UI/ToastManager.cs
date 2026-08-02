using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ToonExporter.UI
{
    // =================================================================
    // ToastManager — UI Toolkit ベースのトースト通知マネージャー
    //
    // シーンに 1 個だけ置く想定。ToonExporterPanel が Awake で見つからなければ
    // 自動で同 GameObject に AddComponent する。
    //
    // 画面下部中央にスタック表示、最大 3 件、3 秒表示 + 0.5 秒フェードで自動消去。
    // Debug.Log も併用して Player.log にも残す。
    //
    // 寿命判定は「絶対時刻の引き算」ではなく「クランプ済みデルタのカウントダウン」。
    // フリーズ明け／Screen.SetResolution 等でエンジン時計が数秒ジャンプしても、
    // 1 フレで消費される寿命は MaxLifetimeDeltaPerFrame までに抑えられるので
    // 表示直後の一撃死が起きない。
    // =================================================================
    public class ToastManager : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // シングルトンアクセス（FindAnyObjectByType で毎回探すのだるいので）
        // ------------------------------------------------------------------

        private static ToastManager _instance;

        /// <summary>シーン内の ToastManager を返す。無ければ null</summary>
        public static ToastManager Instance => _instance;

        // ------------------------------------------------------------------
        // 設定
        // ------------------------------------------------------------------

        private const int MaxToasts = 3;
        private const float DisplayDuration = 3.0f;
        private const float FadeDuration = 0.5f;
        private const float TotalDuration = DisplayDuration + FadeDuration;

        // 1 フレームで消費できる寿命の上限（秒）。時計ジャンプ時の即死回避
        private const float MaxLifetimeDeltaPerFrame = 0.1f;

        // トースト 1 件あたりの高さとマージン
        private const float ToastHeight = 36f;
        private const float ToastMargin = 4f;
        private const float BottomOffset = 60f;

        // ------------------------------------------------------------------
        // レベル定義
        // ------------------------------------------------------------------

        public enum ToastLevel
        {
            Info,
            Success,
            Warning,
            Error,
        }

        // ------------------------------------------------------------------
        // 内部データ
        // ------------------------------------------------------------------

        private struct ToastEntry
        {
            public string Message;
            public ToastLevel Level;
            // 残り寿命（秒）。初描画フレーム（Started=true 化）以降、クランプ済みデルタでカウントダウンする。
            public float Remaining;
            public bool Started;
        }

        private readonly List<ToastEntry> _toasts = new List<ToastEntry>();

        // ------------------------------------------------------------------
        // UI Toolkit 描画層
        // ------------------------------------------------------------------

        // ToonExporterPanel.Awake から注入される描画先レイヤー
        private VisualElement _toastLayer;

        // _toasts と並走するリスト。_toasts[i] の描画担当が _toastElements[i]
        private readonly List<VisualElement> _toastElements = new List<VisualElement>();

        // ------------------------------------------------------------------
        // ライフサイクル
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning("[ToastManager] シーンに複数ある。1 個にして");
                Destroy(this);
                return;
            }
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                // レイヤー配下の VisualElement を全除去して参照クリア
                DetachLayer();
                _instance = null;
            }
        }

        // ------------------------------------------------------------------
        // 公開 API
        // ------------------------------------------------------------------

        /// <summary>
        /// Instance の有無を気にせず呼べる static ヘルパー。
        /// Instance があればトースト表示、なければ Debug.Log に落とす。
        /// 呼び出し側で毎回 if (Instance != null) 書かなくて済む。
        /// </summary>
        public static void ShowOrLog(string message, ToastLevel level = ToastLevel.Info)
        {
            if (_instance != null)
            {
                _instance.Show(message, level);
                return;
            }

            // Instance がない場合は Debug.Log だけ出す
            switch (level)
            {
                case ToastLevel.Error:
                    Debug.LogError($"[Toast] {message}");
                    break;
                case ToastLevel.Warning:
                    Debug.LogWarning($"[Toast] {message}");
                    break;
                default:
                    Debug.Log($"[Toast] {message}");
                    break;
            }
        }

        /// <summary>
        /// トースト表示。Debug.Log も同時に出す。
        /// </summary>
        public void Show(string message, ToastLevel level = ToastLevel.Info)
        {
            // Debug.Log 併用（Player.log に残すため）
            switch (level)
            {
                case ToastLevel.Error:
                    Debug.LogError($"[Toast] {message}");
                    break;
                case ToastLevel.Warning:
                    Debug.LogWarning($"[Toast] {message}");
                    break;
                default:
                    Debug.Log($"[Toast] {message}");
                    break;
            }

            _toasts.Add(new ToastEntry
            {
                Message   = message,
                Level     = level,
                Remaining = TotalDuration,
                // Started=false の間は寿命が減らない（初描画までカウントダウン保留）
                Started   = false,
            });

            // 最大件数超えたら古いの捨てる
            while (_toasts.Count > MaxToasts)
            {
                _toasts.RemoveAt(0);
                // 並走リストの VisualElement も同期して除去
                if (_toastElements.Count > 0)
                {
                    _toastElements[0].RemoveFromHierarchy();
                    _toastElements.RemoveAt(0);
                }
            }
        }

        // ------------------------------------------------------------------
        // UI Toolkit 描画層の注入 / 解除
        // ------------------------------------------------------------------

        /// <summary>
        /// ToonExporterPanel.Awake から呼ばれる。
        /// トースト描画先の VisualElement レイヤーを受け取る。
        /// </summary>
        public void AttachLayer(VisualElement layer)
        {
            _toastLayer = layer;
        }

        /// <summary>
        /// レイヤー配下を全クリア＆参照解除。
        /// OnDestroy + ToonExporterPanel.OnDestroy の双方向で呼べる。
        /// 二重呼びしても壊れないように冪等にしてある。
        /// </summary>
        public void DetachLayer()
        {
            // 並走リストの VisualElement を全除去
            foreach (var elem in _toastElements)
                elem.RemoveFromHierarchy();
            _toastElements.Clear();

            // レイヤー自体も親から外す（残骸防止）
            _toastLayer?.RemoveFromHierarchy();
            _toastLayer = null;
        }

        // ------------------------------------------------------------------
        // Update — _toasts → VisualElement 同期
        // ------------------------------------------------------------------

        private void Update()
        {
            // レイヤー未注入なら描画スキップ（Show 自体は _toasts に貯まり続ける）
            if (_toastLayer == null) return;
            if (_toasts.Count == 0 && _toastElements.Count == 0) return;

            // 寿命消費用デルタ。クランプで時計ジャンプへの免疫を確保
            float dt = Mathf.Min(Time.unscaledDeltaTime, MaxLifetimeDeltaPerFrame);

            // 期限切れを除去（後ろから走査して安全に削除）。
            // Started=false（=まだ描画されてない）の項目は寿命カウントしない。
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                if (_toasts[i].Started && _toasts[i].Remaining <= 0f)
                {
                    _toasts.RemoveAt(i);
                    if (i < _toastElements.Count)
                    {
                        _toastElements[i].RemoveFromHierarchy();
                        _toastElements.RemoveAt(i);
                    }
                }
            }

            if (_toasts.Count == 0) return;

            // 未描画分の VisualElement を生成
            while (_toastElements.Count < _toasts.Count)
            {
                int idx = _toastElements.Count;
                var elem = CreateToastElement(_toasts[idx]);
                _toastLayer.Add(elem);
                _toastElements.Add(elem);
            }

            // 全件の表示状態を更新（フェード・レイアウト位置）
            for (int i = 0; i < _toasts.Count; i++)
            {
                ToastEntry entry = _toasts[i];
                VisualElement elem = _toastElements[i];

                // 初描画フレームは満タンのまま表示、次フレから dt で減らす（表示時間を確実に確保）
                if (!entry.Started)
                    entry.Started = true;
                else
                    entry.Remaining -= dt;
                _toasts[i] = entry;

                // 残り FadeDuration 秒を切ったら線形フェードアウト
                float alpha = Mathf.Clamp01(entry.Remaining / FadeDuration);

                elem.style.opacity = alpha;

                // 下から上へスタック。i=0 が一番古い＝一番上、最新が一番下
                int fromBottom = _toasts.Count - 1 - i;
                float bottomPos = BottomOffset + fromBottom * (ToastHeight + ToastMargin);
                elem.style.bottom = bottomPos;
            }
        }

        // ------------------------------------------------------------------
        // VisualElement 生成ヘルパー
        // ------------------------------------------------------------------

        /// <summary>
        /// トースト 1 件分の VisualElement を生成。
        /// USS クラスでスタイル制御＋レベル別背景色はインラインで設定。
        /// </summary>
        private VisualElement CreateToastElement(ToastEntry entry)
        {
            var item = new VisualElement();
            item.AddToClassList("toon-toast__item");

            switch (entry.Level)
            {
                case ToastLevel.Info:    item.AddToClassList("toon-toast__item--info");    break;
                case ToastLevel.Success: item.AddToClassList("toon-toast__item--success"); break;
                case ToastLevel.Warning: item.AddToClassList("toon-toast__item--warning"); break;
                case ToastLevel.Error:   item.AddToClassList("toon-toast__item--error");   break;
            }

            Color bgColor = GetBackgroundColor(entry.Level);
            item.style.backgroundColor = bgColor;

            // テキスト
            var label = new Label(entry.Message);
            label.AddToClassList("toon-toast__text");
            item.Add(label);

            return item;
        }

        // ------------------------------------------------------------------
        // ヘルパー
        // ------------------------------------------------------------------

        /// <summary>レベルごとの背景色。半透明で被せる</summary>
        private static Color GetBackgroundColor(ToastLevel level)
        {
            switch (level)
            {
                case ToastLevel.Success: return new Color(0.15f, 0.65f, 0.30f, 0.85f);
                case ToastLevel.Warning: return new Color(0.85f, 0.65f, 0.10f, 0.85f);
                case ToastLevel.Error:   return new Color(0.80f, 0.20f, 0.20f, 0.85f);
                default:                 return new Color(0.20f, 0.20f, 0.20f, 0.85f); // Info
            }
        }
    }
}
