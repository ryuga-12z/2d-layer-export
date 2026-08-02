/// <summary>
/// MRT スロット1本分の定義。suffix / SV_Target インデックス / 表示名を束ねた POCO。
/// シェーダーの SV_Target0..4 は焼き固定（バリアント無し）なので
/// ここは「データが記述する」だけ。描画側を動的に組み替える自由度はない。
/// </summary>
public readonly struct ToonLayerSlot
{
    /// <summary>ファイル名サフィックス（例: "shadow1_softlight"）</summary>
    public readonly string suffix;

    /// <summary>MRT の SV_Target インデックス（0〜4）</summary>
    public readonly int svTargetIndex;

    /// <summary>UI 表示用の名前</summary>
    public readonly string displayName;

    public ToonLayerSlot(string suffix, int svTargetIndex, string displayName)
    {
        this.suffix = suffix;
        this.svTargetIndex = svTargetIndex;
        this.displayName = displayName;
    }

    // 既定5スロットカタログ。シェーダー SV_Target0..4 と1:1で対応。
    // 呼び出し側がこの配列のサブセットを渡すだけで書き出し枚数が変わる。
    public static readonly ToonLayerSlot[] DefaultCatalog =
    {
        new ToonLayerSlot("shadow1_softlight", 0, "Shadow 1 (SoftLight)"),
        new ToonLayerSlot("shadow2_softlight", 1, "Shadow 2 (SoftLight)"),
        new ToonLayerSlot("rim_add",           2, "Rim (Add)"),
        new ToonLayerSlot("sub_add",           3, "Sub (Add)"),
        new ToonLayerSlot("comp_reference",    4, "Comp (Reference)"),
    };
}
