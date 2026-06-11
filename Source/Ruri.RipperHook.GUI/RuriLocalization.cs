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

    // ── CABMap (File menu) + title bar ──────────────────────────────
    public static string MenuLoadCabMap => Lang switch
    {
        "zh-Hans" => "加载依赖图(CABMap)…",
        "zh-Hant" => "載入相依圖(CABMap)…",
        _ => "Load CABMap...",
    };

    public static string MenuBuildCabMap => Lang switch
    {
        "zh-Hans" => "构建依赖图(CABMap)…",
        "zh-Hant" => "建立相依圖(CABMap)…",
        _ => "Build CABMap...",
    };

    /// <summary>{0} = CAB count.</summary>
    public static string TitleMapLoaded => Lang switch
    {
        "zh-Hans" => "已加载 map（{0} 个 CAB）",
        "zh-Hant" => "已載入 map（{0} 個 CAB）",
        _ => "map loaded ({0} CABs)",
    };

    public static string TitleMapNone => Lang switch
    {
        "zh-Hans" => "未加载 map",
        "zh-Hant" => "未載入 map",
        _ => "no map loaded",
    };

    /// <summary>{0} = CAB count, {1} = path.</summary>
    public static string CabMapLoaded => Lang switch
    {
        "zh-Hans" => "已加载依赖图：{0} 个 CAB（{1}）",
        "zh-Hant" => "已載入相依圖：{0} 個 CAB（{1}）",
        _ => "CABMap loaded: {0} CABs ({1})",
    };

    public static string CabMapBuildSelectGameFolder => Lang switch
    {
        "zh-Hans" => "选择要构建依赖图的游戏数据目录（如 <名称>_Data）",
        "zh-Hant" => "選擇要建立相依圖的遊戲資料目錄（如 <名稱>_Data）",
        _ => "Select the game data folder to index (e.g. <name>_Data)",
    };

    /// <summary>{0} = folder.</summary>
    public static string CabMapBuilding => Lang switch
    {
        "zh-Hans" => "正在构建依赖图：{0}（逐文件扫描，请稍候）…",
        "zh-Hant" => "正在建立相依圖：{0}（逐檔掃描，請稍候）…",
        _ => "Building CABMap over {0} (scanning one file at a time)...",
    };

    /// <summary>{0} = CAB count, {1} = path.</summary>
    public static string CabMapBuilt => Lang switch
    {
        "zh-Hans" => "依赖图构建完成：{0} 个 CAB（{1}）",
        "zh-Hant" => "相依圖建立完成：{0} 個 CAB（{1}）",
        _ => "CABMap built: {0} CABs ({1})",
    };

    public static string CabMapBuildFailed => Lang switch
    {
        "zh-Hans" => "构建依赖图失败。",
        "zh-Hant" => "建立相依圖失敗。",
        _ => "Building CABMap failed.",
    };

    // ── 按类型导出 ──────────────────────────────────────────────────
    public static string MenuByTypeExport => Lang switch
    {
        "zh-Hans" => "按类型导出…（需先加载 map）",
        "zh-Hant" => "依類型匯出…（需先載入 map）",
        _ => "Export by Type... (needs a CABMap)",
    };

    public static string ByTypePickHint => Lang switch
    {
        "zh-Hans" => "勾选要批量导出的资产类型：",
        "zh-Hant" => "勾選要批次匯出的資產類型：",
        _ => "Tick the asset types to batch-export:",
    };

    public static string ByTypeExportCaption => Lang switch
    {
        "zh-Hans" => "按类型导出",
        "zh-Hant" => "依類型匯出",
        _ => "Export by Type",
    };

    public static string ByTypeExportPreparing => Lang switch
    {
        "zh-Hans" => "按类型导出：准备中（按 map 精准加载目标 bundle）…",
        "zh-Hant" => "依類型匯出：準備中（依 map 精準載入目標 bundle）…",
        _ => "Export by type: preparing (loading only matching bundles via the map)...",
    };

    /// <summary>{0} = load label.</summary>
    public static string ByTypeExportLoading => Lang switch
    {
        "zh-Hans" => "按类型导出：加载 {0} 个 bundle…",
        "zh-Hant" => "依類型匯出：載入 {0} 個 bundle…",
        _ => "Export by type: loading {0} bundle(s)...",
    };

    /// <summary>{0} = output path.</summary>
    public static string ByTypeExportExporting => Lang switch
    {
        "zh-Hans" => "按类型导出：导出到 {0} …",
        "zh-Hant" => "依類型匯出：匯出到 {0} …",
        _ => "Export by type: exporting to {0}...",
    };

    /// <summary>{0} = output path.</summary>
    public static string ByTypeExportDone => Lang switch
    {
        "zh-Hans" => "按类型导出完成：{0}",
        "zh-Hant" => "依類型匯出完成：{0}",
        _ => "Export by type finished: {0}",
    };

    public static string ByTypeExportFailedCaption => Lang switch
    {
        "zh-Hans" => "按类型导出失败",
        "zh-Hant" => "依類型匯出失敗",
        _ => "Export by type failed",
    };

    public static string ByTypeExportFailedStatus => Lang switch
    {
        "zh-Hans" => "按类型导出失败。",
        "zh-Hant" => "依類型匯出失敗。",
        _ => "Export by type failed.",
    };

    public static string NoBundlesForTypes => Lang switch
    {
        "zh-Hans" => "依赖图里没有包含所选类型的 bundle。",
        "zh-Hant" => "相依圖裡沒有包含所選類型的 bundle。",
        _ => "The CABMap has no bundles containing the selected type(s).",
    };

    // ── 右键：连同依赖一起导出 ──────────────────────────────────────
    public static string ContextExportWithDeps => Lang switch
    {
        "zh-Hans" => "导出（含全部依赖）…",
        "zh-Hant" => "匯出（含全部相依）…",
        _ => "Export (with all dependencies)...",
    };

    public static string WithDepsCaption => Lang switch
    {
        "zh-Hans" => "连同依赖导出",
        "zh-Hant" => "連同相依匯出",
        _ => "Export with dependencies",
    };

    public static string WithDepsNoSource => Lang switch
    {
        "zh-Hans" => "选中资产在依赖图里找不到来源 bundle（map 是否对应当前游戏？）。",
        "zh-Hant" => "選取資產在相依圖裡找不到來源 bundle（map 是否對應目前遊戲？）。",
        _ => "No source bundle for the selected asset(s) in the CABMap (is the map for this game?).",
    };

    public static string WithDepsPreparing => Lang switch
    {
        "zh-Hans" => "连同依赖导出：准备中…",
        "zh-Hant" => "連同相依匯出：準備中…",
        _ => "Export with dependencies: preparing...",
    };

    /// <summary>{0} = load label.</summary>
    public static string WithDepsLoading => Lang switch
    {
        "zh-Hans" => "连同依赖导出：加载 {0} 个 bundle…",
        "zh-Hant" => "連同相依匯出：載入 {0} 個 bundle…",
        _ => "Export with dependencies: loading {0} bundle(s)...",
    };

    /// <summary>{0} = output path.</summary>
    public static string WithDepsExporting => Lang switch
    {
        "zh-Hans" => "连同依赖导出：导出到 {0} …",
        "zh-Hant" => "連同相依匯出：匯出到 {0} …",
        _ => "Export with dependencies: exporting to {0}...",
    };

    /// <summary>{0} = output path.</summary>
    public static string WithDepsDone => Lang switch
    {
        "zh-Hans" => "连同依赖导出完成：{0}",
        "zh-Hant" => "連同相依匯出完成：{0}",
        _ => "Export with dependencies finished: {0}",
    };

    public static string WithDepsFailedCaption => Lang switch
    {
        "zh-Hans" => "连同依赖导出失败",
        "zh-Hant" => "連同相依匯出失敗",
        _ => "Export with dependencies failed",
    };

    public static string WithDepsFailedStatus => Lang switch
    {
        "zh-Hans" => "连同依赖导出失败。",
        "zh-Hant" => "連同相依匯出失敗。",
        _ => "Export with dependencies failed.",
    };
}
