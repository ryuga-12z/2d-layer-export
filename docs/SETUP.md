# セットアップガイド

UnityEditorでの手動セットアップ手順です。

---

## 動作環境

| | |
|---|---|
| Unity | 6.3 以降 |
| レンダーパイプライン | URP 17 |
| カラースペース | Linear |
| 対応プラットフォーム | Windows（他プラットフォームは未検証） |

---

## 依存パッケージ

**[unity-standalone-file-browser](https://github.com/Sov3rain/unity-standalone-file-browser)（Sov3rain fork）** — ネイティブのファイルダイアログを開くために使用します。

Package Manager の「Add package from git URL」から導入するか、`Packages/manifest.json` に追加してください。

```json
"com.gkngkc.usfb": "https://github.com/Sov3rain/unity-standalone-file-browser.git?path=/Assets/StandaloneFileBrowser"
```

`ToonExporter.Runtime.asmdef` が `USFB.Runtime` を参照しているので、これが無いと動きません。



## URP の設定

### 1. Renderer Feature を登録する

1. 使用中の UniversalRendererDataアセットを選択（通常は `Assets/Settings/` 配下）
2. Inspector 下部の **Add Renderer Feature** → **ToonMRTRendererFeature**
3. 設定項目はどちらも初期値のままで動きます
   - Target Layer Mask: `Everything`
   - Override Width / Height: `0`（0 のときはカメラ解像度を使用。画像読み込み時に自動で書き換わります）

これを登録しないと MRT パスが走らず、書き出しが失敗します。

### 2. Always Include Shaders に追加する

1. **Edit → Project Settings → Graphics**
2. **Always Include Shaders** のリストを 1 つ増やす
3. 空きスロットに **`ToonExporter/Toon2DLayered`** をアサイン

MRT パスは Renderer Feature 経由の独自パスなので、Unity が「どのカメラからも参照されていない」と判断してビルド時にシェーダーを除外してしまうことがあります。ここに登録しておけば確実にビルドに含まれます。

---

## アセンブリ構成

3 つの asmdef に分かれています。

| asmdef | 中身 | 依存 |
|---|---|---|
| `ToonExporter.Core` | MRT 焼き出しコア / State / プリセット永続化 | URP のみ |
| `ToonExporter.Editor` | エディタウィンドウ | Core + UnityEditor。Editor プラットフォームのみ |
| `ToonExporter.Runtime` | UI Toolkit のパネル / 各コントローラ | Core + USFB |

Core が UnityEditor に依存しないので、ビルドした実行ファイルでも書き出し処理がそのまま動きます。

---

## シーン

 `Assets/Scenes/2DToon_LayerExport.unity` がそのまま完成形です。自分で組む場合は以下。



```
2DToonManager   　← UIDocument + 各コンポーネント一式
Quad            　← プレビュー表示面。Toon2DLayeredシェーダーを付与したマテリアルを貼る
Main Camera     　← Orthographic 。Position(0, 0, -1) 
```

### 2DToonManager

空の GameObject に以下を全部アタッチします。

| コンポーネント | 役割 |
|---|---|
| `UIDocument` | UI Toolkit のパネル描画 |
| `ToonExporterPanel` | UI の構築とパラメータ管理 |
| `ToonPreviewController` | UI の値をマテリアルへ反映 |
| `ToonImageImporter` | 画像の読み込みとウィンドウのアスペクト調整 |
| `ExportController` | 書き出し処理の統括 |

### Inspector のアサイン一覧

各コンポーネントの参照は下記。
**UIDocument**

Panel Settings : `Assets/ToonExporter/Runtime/UI/ToonExportPanel.asset`
Source Asset : `Assets/ToonExporter/Runtime/UI/ToonExporterPanel.uxml`

**ToonExporterPanel**

Ui Document : 同じ GameObject の `UIDocument`
Export Controller : 同じ GameObject の `ExportController`
Image Importer : 同じ GameObject の `ToonImageImporter`

**ToonPreviewController**

Target Renderer : `Quad` の Renderer
Panel : 同じ GameObject の `ToonExporterPanel`

**ToonImageImporter**

Target Quad : `Quad` の Renderer（`ToonPreviewController` と同じものを指すこと）
Target Camera : `Main Camera`
Preview Controller : 同じ GameObject の `ToonPreviewController`
Renderer Data : 使用中の `UniversalRendererData`

**ExportController**

Renderer Data : 使用中の `UniversalRendererData`
Preview Controller : 同じ GameObject の `ToonPreviewController`
Image Importer : 同じ GameObject の `ToonImageImporter`
Default Base Name : `toon`（保存ダイアログの初期ファイル名。任意で変更してください）


---

## エディタから書き出し

Unity 内から直接書き出す最小のウィンドウが付いています。

**Tools → Toon Exporter → Export MRT Layers**

「Export Layers」ボタンを押すと `Assets/Output/` に png が 5 枚出力されます。

※Play モードで 1 フレーム以上描画してから実行する必要があります
陰影の設定はマテリアルの Inspector で直接いじる形になります。
スライダーで調整しながら書き出したい場合は、Play モードに入ってツール本体の UI を使用してください。
