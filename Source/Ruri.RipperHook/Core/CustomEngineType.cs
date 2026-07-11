// Unity 版本号f255是最大值 只能支持255个自定义引擎游戏
public enum CustomEngineType : byte
{
    // Based RazTools
    // Numeric values are pinned: they double as folder names under the external TypeTree
    // pipeline (see FRAMEWORK.md §10). Houkai=1 retired; do not renumber StarRail/ExAstris/EndField.
    Genshit = 0,
    StarRail = 2,
    ZenlessZoneZero = 3,

	// FractalTools
    ExAstris = 4,
    EndField = 5,

    Max = 255,
}