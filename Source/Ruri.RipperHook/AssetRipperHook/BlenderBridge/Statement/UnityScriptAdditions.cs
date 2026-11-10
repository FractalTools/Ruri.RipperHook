using System.Text.Json.Nodes;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// What a script record states beyond the fields of its script, and what of an engine component it leaves out, both as data
/// keyed by the class: a registered class gets an addition computed from its own record (an asset format its package reads),
/// and the members of an engine component that are the hierarchy's rather than the component's are dropped. A class that
/// is in neither table is stated as it is.
/// </summary>
internal static class UnityScriptAdditions
{
    private const string InputSystemAssembly = "Unity.InputSystem";
    private const string InputActionAssetClass = "InputActionAsset";

    private delegate void Addition(JsonObject record, string name);

    private static readonly IReadOnlySet<string> ObjectHeader = new HashSet<string>(StringComparer.Ordinal)
    {
        "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_GameObject",
    };

    private static readonly IReadOnlySet<string> TransformHierarchy = new HashSet<string>(ObjectHeader, StringComparer.Ordinal)
    {
        "m_Children", "m_Father", "m_RootOrder", "m_LocalEulerAnglesHint",
    };

    private static readonly Dictionary<string, IReadOnlySet<string>> Dropping = new(StringComparer.Ordinal)
    {
        ["Transform"] = TransformHierarchy,
        ["RectTransform"] = TransformHierarchy,
    };

    private static readonly Dictionary<(string Assembly, string Class), Addition> Additions = new()
    {
        [(InputSystemAssembly, InputActionAssetClass)] = UnityInputActions.Add,
    };

    /// <summary>The members of an engine component of this class that are not stated.</summary>
    public static IReadOnlySet<string> Dropped(string className) =>
        Dropping.TryGetValue(className, out IReadOnlySet<string>? dropped) ? dropped : ObjectHeader;

    /// <summary>Add what the class is registered to add to its record.</summary>
    public static void Add(JsonObject record, string assembly, string className, string name)
    {
        if (Additions.TryGetValue((assembly, className), out Addition? addition))
        {
            addition(record, name);
        }
    }
}
