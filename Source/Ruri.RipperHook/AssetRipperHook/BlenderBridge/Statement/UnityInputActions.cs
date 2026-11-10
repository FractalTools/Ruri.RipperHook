using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// An input action asset as the JSON text its package writes (InputActionAsset.ToJson), made from the members the package
/// serializes: a package that reads the asset back reads this text (InputActionAsset.FromJson) and needs no other form of it.
/// The shape and the order of the members are the package's own (InputActionAsset.cs WriteFileJson, InputActionMap.cs
/// WriteMapJson and WriteActionJson and BindingJson, InputControlScheme.cs SchemeJson); a member the asset does not carry is
/// the package's default for it.
/// </summary>
internal static class UnityInputActions
{
    private const int JsonVersionCurrent = 1;
    private const int JsonVersionOriginal = 0;
    private const int ActionWantsInitialStateCheck = 1 << 0;
    private const int BindingComposite = 1 << 2;
    private const int BindingPartOfComposite = 1 << 3;
    private const int DeviceOptional = 1 << 0;
    private const int DeviceOr = 1 << 1;

    private static readonly string[] ActionTypes = ["Value", "Button", "PassThrough"];

    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        IndentSize = 4,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Add(JsonObject record, string name)
    {
        JsonObject fields = record["fields"] as JsonObject
            ?? throw new InvalidOperationException("an input action asset record has no fields.");
        JsonArray maps = [];
        if (fields["m_ActionMaps"] is JsonArray actionMaps)
        {
            foreach (JsonNode? map in actionMaps)
            {
                maps.Add(Map(Object(map, "an action map")));
            }
        }
        JsonArray schemes = [];
        if (fields["m_ControlSchemes"] is JsonArray controlSchemes)
        {
            foreach (JsonNode? scheme in controlSchemes)
            {
                schemes.Add(Scheme(Object(scheme, "a control scheme")));
            }
        }
        JsonObject file = new()
        {
            ["version"] = maps.Count > 0 || schemes.Count > 0 ? JsonVersionCurrent : JsonVersionOriginal,
            ["name"] = name,
            ["maps"] = maps,
            ["controlSchemes"] = schemes,
        };
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, Options))
        {
            file.WriteTo(writer);
        }
        record["json"] = System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static JsonObject Map(JsonObject map)
    {
        JsonArray actions = [];
        if (map["m_Actions"] is JsonArray stated)
        {
            foreach (JsonNode? action in stated)
            {
                actions.Add(Action(Object(action, "an action")));
            }
        }
        JsonArray bindings = [];
        if (map["m_Bindings"] is JsonArray statedBindings)
        {
            foreach (JsonNode? binding in statedBindings)
            {
                bindings.Add(Binding(Object(binding, "a binding")));
            }
        }
        return new JsonObject
        {
            ["name"] = Text(map, "m_Name"),
            ["id"] = Text(map, "m_Id"),
            ["actions"] = actions,
            ["bindings"] = bindings,
        };
    }

    private static JsonObject Action(JsonObject action)
    {
        if (action["m_SingletonActionBindings"] is JsonArray { Count: > 0 })
        {
            throw new InvalidOperationException($"the action '{Text(action, "m_Name")}' carries bindings of its own, which an action map file does not write.");
        }
        int type = Number(action, "m_Type");
        if (type < 0 || type >= ActionTypes.Length)
        {
            throw new InvalidOperationException($"the action '{Text(action, "m_Name")}' has the type {type}, which the package does not name.");
        }
        return new JsonObject
        {
            ["name"] = Text(action, "m_Name"),
            ["type"] = ActionTypes[type],
            ["id"] = Text(action, "m_Id"),
            ["expectedControlType"] = Text(action, "m_ExpectedControlType"),
            ["processors"] = Text(action, "m_Processors"),
            ["interactions"] = Text(action, "m_Interactions"),
            ["initialStateCheck"] = (Number(action, "m_Flags") & ActionWantsInitialStateCheck) != 0,
            ["priority"] = Number(action, "m_Priority"),
        };
    }

    private static JsonObject Binding(JsonObject binding)
    {
        int flags = Number(binding, "m_Flags");
        return new JsonObject
        {
            ["name"] = Text(binding, "m_Name"),
            ["id"] = Text(binding, "m_Id"),
            ["path"] = Text(binding, "m_Path"),
            ["interactions"] = Text(binding, "m_Interactions"),
            ["processors"] = Text(binding, "m_Processors"),
            ["groups"] = Text(binding, "m_Groups"),
            ["action"] = Text(binding, "m_Action"),
            ["isComposite"] = (flags & BindingComposite) != 0,
            ["isPartOfComposite"] = (flags & BindingPartOfComposite) != 0,
        };
    }

    private static JsonObject Scheme(JsonObject scheme)
    {
        JsonArray devices = [];
        if (scheme["m_DeviceRequirements"] is JsonArray requirements)
        {
            foreach (JsonNode? requirement in requirements)
            {
                JsonObject device = Object(requirement, "a device requirement");
                int flags = Number(device, "m_Flags");
                devices.Add(new JsonObject
                {
                    ["devicePath"] = Text(device, "m_ControlPath"),
                    ["isOptional"] = (flags & DeviceOptional) != 0,
                    ["isOR"] = (flags & DeviceOr) != 0,
                });
            }
        }
        return new JsonObject
        {
            ["name"] = Text(scheme, "m_Name"),
            ["bindingGroup"] = Text(scheme, "m_BindingGroup"),
            ["devices"] = devices,
        };
    }

    private static JsonObject Object(JsonNode? node, string what) =>
        node as JsonObject ?? throw new InvalidOperationException($"{what} of an input action asset is not an object.");

    private static string Text(JsonObject owner, string member) =>
        owner[member] is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;

    private static int Number(JsonObject owner, string member) =>
        owner[member] is JsonValue value && value.TryGetValue(out int number) ? number : 0;
}
