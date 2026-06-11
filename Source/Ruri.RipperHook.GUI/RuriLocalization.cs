using AssetRipper.GUI.Localizations;

namespace Ruri.RipperHook.GUI;

/// <summary>
/// Ruri GUI 自有界面字符串的本地化表。手写版，刻意对齐 AssetRipper 的
/// <see cref="Localization"/>（源生成）的用法——每个属性按
/// <see cref="Localization.CurrentLanguageCode"/> 选择语言，跟随用户在设置里选的语言。
/// 这样菜单/对话框里就不会出现写死的中文明文。新增语言只要在 switch 里加分支即可。
/// </summary>
internal static class RuriLocalization
{
    private static string Lang => Localization.CurrentLanguageCode;

    // ── 快速导出（原 Direct Export）────────────────────────────────
    public static string MenuQuickExport => Lang switch
    {
        "zh-Hans" => "快速导出",
        "zh-Hant" => "快速匯出",
        _ => "Quick Export",
    };

    public static string MenuQuickExportFromFile => Lang switch
    {
        "zh-Hans" => "从文件…",
        "zh-Hant" => "從檔案…",
        _ => "From file(s)...",
    };

    public static string MenuQuickExportFromFolder => Lang switch
    {
        "zh-Hans" => "从文件夹…",
        "zh-Hant" => "從資料夾…",
        _ => "From folder...",
    };

    // ── 仅导出代码（忽略所有资产）──────────────────────────────────
    public static string MenuCodeExport => Lang switch
    {
        "zh-Hans" => "仅导出代码",
        "zh-Hant" => "僅匯出程式碼",
        _ => "Export Code Only",
    };

    public static string MenuCodeExportFromFolder => Lang switch
    {
        "zh-Hans" => "从游戏目录导出（全部 IL2CPP 代码，跳过资产）…",
        "zh-Hant" => "從遊戲目錄匯出（全部 IL2CPP 程式碼，略過資產）…",
        _ => "From game folder (all IL2CPP code, skip assets)...",
    };

    public static string CodeExportSelectGameFolder => Lang switch
    {
        "zh-Hans" => "选择要导出代码的游戏根目录（需含 <名称>.exe / GameAssembly.dll / <名称>_Data）",
        "zh-Hant" => "選擇要匯出程式碼的遊戲根目錄（需含 <名稱>.exe / GameAssembly.dll / <名稱>_Data）",
        _ => "Select the game root folder (must contain <name>.exe / GameAssembly.dll / <name>_Data)",
    };

    public static string CodeExportSelectOutputFolder => Lang switch
    {
        "zh-Hans" => "选择输出目录（已有内容会被清空）",
        "zh-Hant" => "選擇輸出目錄（既有內容會被清空）",
        _ => "Select the output folder (existing contents will be cleared)",
    };

    public static string CodeExportCaption => Lang switch
    {
        "zh-Hans" => "仅导出代码",
        "zh-Hant" => "僅匯出程式碼",
        _ => "Export Code Only",
    };

    /// <summary>{0} = output path.</summary>
    public static string CodeExportOutputNonEmpty => Lang switch
    {
        "zh-Hans" => "输出目录已存在且非空：\n{0}\n\n清空其内容并继续？",
        "zh-Hant" => "輸出目錄已存在且非空：\n{0}\n\n清空其內容並繼續？",
        _ => "Output folder already exists and is non-empty:\n{0}\n\nDelete its contents and continue?",
    };

    public static string CodeExportCancelled => Lang switch
    {
        "zh-Hans" => "已取消导出代码。",
        "zh-Hant" => "已取消匯出程式碼。",
        _ => "Code export aborted.",
    };

    public static string CodeExportPreparing => Lang switch
    {
        "zh-Hans" => "导出代码：准备中（启用 IL2CPP 反汇编 + 仅代码过滤）…",
        "zh-Hant" => "匯出程式碼：準備中（啟用 IL2CPP 反組譯 + 僅程式碼過濾）…",
        _ => "Export code: preparing (IL2CPP disassembly + code-only filter)...",
    };

    /// <summary>{0} = load label.</summary>
    public static string CodeExportLoading => Lang switch
    {
        "zh-Hans" => "导出代码：加载 {0} …",
        "zh-Hant" => "匯出程式碼：載入 {0} …",
        _ => "Export code: loading {0}...",
    };

    /// <summary>{0} = output path.</summary>
    public static string CodeExportExporting => Lang switch
    {
        "zh-Hans" => "导出代码：导出到 {0}（仅代码）…",
        "zh-Hant" => "匯出程式碼：匯出到 {0}（僅程式碼）…",
        _ => "Export code: exporting to {0} (code only)...",
    };

    /// <summary>{0} = output path.</summary>
    public static string CodeExportDone => Lang switch
    {
        "zh-Hans" => "导出代码完成：{0}",
        "zh-Hant" => "匯出程式碼完成：{0}",
        _ => "Code export finished: {0}",
    };

    public static string CodeExportFailedCaption => Lang switch
    {
        "zh-Hans" => "导出代码失败",
        "zh-Hant" => "匯出程式碼失敗",
        _ => "Code export failed",
    };

    public static string CodeExportFailedStatus => Lang switch
    {
        "zh-Hans" => "导出代码失败。",
        "zh-Hant" => "匯出程式碼失敗。",
        _ => "Code export failed.",
    };

    /// <summary>{0} = output path.</summary>
    public static string CodeExportOutputInsideInput => Lang switch
    {
        "zh-Hans" => "输出目录不能是输入目录或其父目录：{0}",
        "zh-Hant" => "輸出目錄不能是輸入目錄或其父目錄：{0}",
        _ => "The output folder cannot be the input folder or a parent of it: {0}",
    };
}
