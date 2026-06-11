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

    // ── 各“仅导出 X”功能共用的对话框文案 ───────────────────────────
    public static string ExportSelectGameFolder => Lang switch
    {
        "zh-Hans" => "选择游戏根目录（需含 <名称>.exe / GameAssembly.dll / <名称>_Data）",
        "zh-Hant" => "選擇遊戲根目錄（需含 <名稱>.exe / GameAssembly.dll / <名稱>_Data）",
        _ => "Select the game root folder (must contain <name>.exe / GameAssembly.dll / <name>_Data)",
    };

    public static string ExportSelectOutputFolder => Lang switch
    {
        "zh-Hans" => "选择输出目录（已有内容会被清空）",
        "zh-Hant" => "選擇輸出目錄（既有內容會被清空）",
        _ => "Select the output folder (existing contents will be cleared)",
    };

    /// <summary>{0} = output path.</summary>
    public static string ExportOutputNonEmpty => Lang switch
    {
        "zh-Hans" => "输出目录已存在且非空：\n{0}\n\n清空其内容并继续？",
        "zh-Hant" => "輸出目錄已存在且非空：\n{0}\n\n清空其內容並繼續？",
        _ => "Output folder already exists and is non-empty:\n{0}\n\nDelete its contents and continue?",
    };

    /// <summary>{0} = output path.</summary>
    public static string ExportOutputInsideInput => Lang switch
    {
        "zh-Hans" => "输出目录不能是输入目录或其父目录：{0}",
        "zh-Hant" => "輸出目錄不能是輸入目錄或其父目錄：{0}",
        _ => "The output folder cannot be the input folder or a parent of it: {0}",
    };

    public static string ExportCancelled => Lang switch
    {
        "zh-Hans" => "已取消导出。",
        "zh-Hant" => "已取消匯出。",
        _ => "Export aborted.",
    };

    // ── 导出反汇编（原 Export Code Only / CodeOnlyExport）───────────
    public static string MenuDisassemblyExport => Lang switch
    {
        "zh-Hans" => "导出反汇编",
        "zh-Hant" => "匯出反組譯",
        _ => "Export Disassembly",
    };

    public static string MenuDisassemblyExportFromFolder => Lang switch
    {
        "zh-Hans" => "从游戏目录导出（全部代码 + IL2CPP 反汇编，跳过资产）…",
        "zh-Hant" => "從遊戲目錄匯出（全部程式碼 + IL2CPP 反組譯，略過資產）…",
        _ => "From game folder (all code + IL2CPP asm, skip assets)...",
    };

    public static string DisassemblyExportCaption => Lang switch
    {
        "zh-Hans" => "导出反汇编",
        "zh-Hant" => "匯出反組譯",
        _ => "Export Disassembly",
    };

    public static string DisassemblyExportPreparing => Lang switch
    {
        "zh-Hans" => "导出反汇编：准备中（启用 IL2CPP 反汇编 + 仅代码过滤）…",
        "zh-Hant" => "匯出反組譯：準備中（啟用 IL2CPP 反組譯 + 僅程式碼過濾）…",
        _ => "Export disassembly: preparing (IL2CPP disassembly + code-only filter)...",
    };

    /// <summary>{0} = load label.</summary>
    public static string DisassemblyExportLoading => Lang switch
    {
        "zh-Hans" => "导出反汇编：加载 {0} …",
        "zh-Hant" => "匯出反組譯：載入 {0} …",
        _ => "Export disassembly: loading {0}...",
    };

    /// <summary>{0} = output path.</summary>
    public static string DisassemblyExportExporting => Lang switch
    {
        "zh-Hans" => "导出反汇编：导出到 {0} …",
        "zh-Hant" => "匯出反組譯：匯出到 {0} …",
        _ => "Export disassembly: exporting to {0}...",
    };

    /// <summary>{0} = output path.</summary>
    public static string DisassemblyExportDone => Lang switch
    {
        "zh-Hans" => "导出反汇编完成：{0}",
        "zh-Hant" => "匯出反組譯完成：{0}",
        _ => "Disassembly export finished: {0}",
    };

    public static string DisassemblyExportFailedCaption => Lang switch
    {
        "zh-Hans" => "导出反汇编失败",
        "zh-Hant" => "匯出反組譯失敗",
        _ => "Disassembly export failed",
    };

    public static string DisassemblyExportFailedStatus => Lang switch
    {
        "zh-Hans" => "导出反汇编失败。",
        "zh-Hant" => "匯出反組譯失敗。",
        _ => "Disassembly export failed.",
    };

    // ── 导出全部着色器 ──────────────────────────────────────────────
    public static string MenuShaderExport => Lang switch
    {
        "zh-Hans" => "导出全部着色器",
        "zh-Hant" => "匯出全部著色器",
        _ => "Export All Shaders",
    };

    public static string MenuShaderExportFromFolder => Lang switch
    {
        "zh-Hans" => "从游戏目录导出（反编译，跳过其它资产）…",
        "zh-Hant" => "從遊戲目錄匯出（反編譯，略過其它資產）…",
        _ => "From game folder (decompiled, skip other assets)...",
    };

    public static string ShaderExportCaption => Lang switch
    {
        "zh-Hans" => "导出全部着色器",
        "zh-Hant" => "匯出全部著色器",
        _ => "Export All Shaders",
    };

    public static string ShaderExportPreparing => Lang switch
    {
        "zh-Hans" => "导出着色器：准备中（启用着色器反编译 + 仅着色器过滤）…",
        "zh-Hant" => "匯出著色器：準備中（啟用著色器反編譯 + 僅著色器過濾）…",
        _ => "Export shaders: preparing (shader decompiler + shaders-only filter)...",
    };

    /// <summary>{0} = load label.</summary>
    public static string ShaderExportLoading => Lang switch
    {
        "zh-Hans" => "导出着色器：加载 {0} …",
        "zh-Hant" => "匯出著色器：載入 {0} …",
        _ => "Export shaders: loading {0}...",
    };

    /// <summary>{0} = output path.</summary>
    public static string ShaderExportExporting => Lang switch
    {
        "zh-Hans" => "导出着色器：导出到 {0} …",
        "zh-Hant" => "匯出著色器：匯出到 {0} …",
        _ => "Export shaders: exporting to {0}...",
    };

    /// <summary>{0} = output path.</summary>
    public static string ShaderExportDone => Lang switch
    {
        "zh-Hans" => "导出着色器完成：{0}",
        "zh-Hant" => "匯出著色器完成：{0}",
        _ => "Shader export finished: {0}",
    };

    public static string ShaderExportFailedCaption => Lang switch
    {
        "zh-Hans" => "导出着色器失败",
        "zh-Hant" => "匯出著色器失敗",
        _ => "Shader export failed",
    };

    public static string ShaderExportFailedStatus => Lang switch
    {
        "zh-Hans" => "导出着色器失败。",
        "zh-Hant" => "匯出著色器失敗。",
        _ => "Shader export failed.",
    };
}
