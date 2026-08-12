using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_90;
using AssetRipper.SourceGenerated.Subclasses.Axes;
using AssetRipper.SourceGenerated.Subclasses.Human;
using AssetRipper.SourceGenerated.Subclasses.Skeleton;
using AssetRipper.SourceGenerated.Subclasses.SkeletonPose;
using AssetRipper.SourceGenerated.Subclasses.Vector3Float;
using AssetRipper.SourceGenerated.Subclasses.Vector4Float;
using AssetRipper.SourceGenerated.Subclasses.Xform;

namespace Ruri.RipperHook.Humanoid;

/// <summary>
/// Everything a muscle referential is built FROM, source-neutral: the same numbers arrive either
/// as AssetRipper's typed <see cref="IAvatar"/> (an asset loaded in-pipeline) or as the avatar's
/// serialized document tree in JSON (Unity's own field names, stamped onto a Blender armature and
/// handed back across the bridge at solve time). One build path consumes this; the two factories
/// below are pure field extraction and hold no muscle knowledge of their own.
/// </summary>
public sealed class AvatarRigInput
{
    /// <summary>Per raw human-skeleton node (m_Human.m_Skeleton.m_Node): parent index and axes index.</summary>
    public required int[] NodeParent { get; init; }
    public required int[] NodeAxesId { get; init; }

    /// <summary>CRC32 path ids per node (m_Skeleton.m_ID), the key space of <see cref="Tos"/>.</summary>
    public required uint[] NodeId { get; init; }

    /// <summary>m_Skeleton.m_AxesArray: the muscle referential rows nodes point into via NodeAxesId.</summary>
    public required AxesRow[] Axes { get; init; }

    /// <summary>m_TOS: CRC32 path hash -> full transform path.</summary>
    public required Dictionary<uint, string> Tos { get; init; }

    public required int[] HumanBoneIndex { get; init; }
    public required int[] LeftHandBoneIndex { get; init; }
    public required int[] RightHandBoneIndex { get; init; }
    public required float[] HumanBoneMass { get; init; }

    /// <summary>m_Human.m_RootX.q: the rest root orientation the RootQ channel is relative to.</summary>
    public required Quaternion RootRestQ { get; init; }

    public required float ArmTwist { get; init; }
    public required float ForeArmTwist { get; init; }
    public required float UpperLegTwist { get; init; }
    public required float LegTwist { get; init; }

    /// <summary>m_Human.m_SkeletonPose.m_X: per-node local rest (t, q), the provisional-FK frame.</summary>
    public required (Vector3 T, Quaternion Q)[] SkeletonPose { get; init; }

    public readonly record struct AxesRow(
        Quaternion PreQ, Quaternion PostQ, Vector3 Sgn, Vector3 LimitMin, Vector3 LimitMax);

    // ── typed-asset factory ──────────────────────────────────────────────────

    public static AvatarRigInput FromAvatar(IAvatar avatar)
    {
        IHuman human = avatar.Avatar.Human.Data;
        ISkeleton skeleton = human.Skeleton.Data;

        int nodeCount = skeleton.Node.Count;
        int[] nodeParent = new int[nodeCount];
        int[] nodeAxesId = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            nodeParent[i] = skeleton.Node[i].ParentId;
            nodeAxesId[i] = skeleton.Node[i].AxesId;
        }

        uint[] nodeId = new uint[skeleton.ID.Count];
        for (int i = 0; i < nodeId.Length; i++)
        {
            nodeId[i] = skeleton.ID[i];
        }

        AxesRow[] axes = new AxesRow[skeleton.AxesArray.Count];
        for (int i = 0; i < axes.Length; i++)
        {
            IAxes entry = skeleton.AxesArray[i];
            axes[i] = new AxesRow(
                ToQuaternion(entry.PreQ),
                ToQuaternion(entry.PostQ),
                GetSgn(entry),
                GetLimit(entry, min: true),
                GetLimit(entry, min: false));
        }

        Dictionary<uint, string> tos = new(avatar.TOS.Count);
        foreach (var pair in avatar.TOS)
        {
            Utf8String? path = pair.Value;
            if (path is not null && !path.IsEmpty)
            {
                tos[pair.Key] = path.String;
            }
        }

        float[] mass = new float[human.HumanBoneMass.Count];
        for (int i = 0; i < mass.Length; i++)
        {
            mass[i] = human.HumanBoneMass[i];
        }

        ISkeletonPose skeletonPose = human.SkeletonPose.Data;
        (Vector3, Quaternion)[] pose = new (Vector3, Quaternion)[skeletonPose.X.Count];
        for (int i = 0; i < pose.Length; i++)
        {
            IXform xform = skeletonPose.X[i];
            pose[i] = (ToXformTranslation(xform), ToQuaternion(xform.Q));
        }

        return new AvatarRigInput
        {
            NodeParent = nodeParent,
            NodeAxesId = nodeAxesId,
            NodeId = nodeId,
            Axes = axes,
            Tos = tos,
            HumanBoneIndex = ToIntArray(human.HumanBoneIndex),
            LeftHandBoneIndex = ToIntArray(human.LeftHand.Data.HandBoneIndex),
            RightHandBoneIndex = ToIntArray(human.RightHand.Data.HandBoneIndex),
            HumanBoneMass = mass,
            RootRestQ = ToQuaternion(human.RootX.Q),
            ArmTwist = human.ArmTwist,
            ForeArmTwist = human.ForeArmTwist,
            UpperLegTwist = human.UpperLegTwist,
            LegTwist = human.LegTwist,
            SkeletonPose = pose,
        };
    }

    private static int[] ToIntArray(AssetRipper.Assets.Generics.AssetList<int> list)
    {
        int[] result = new int[list.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = list[i];
        }
        return result;
    }

    private static Quaternion ToQuaternion(IVector4Float v) => new(v.X, v.Y, v.Z, v.W);

    private static Vector3 GetSgn(IAxes axes)
    {
        if (axes.Has_Sgn_Vector3Float())
        {
            return ToVector3(axes.Sgn_Vector3Float);
        }
        Vector4Float? sgn4 = axes.Sgn_Vector4Float;
        return sgn4 is null ? Vector3.One : new Vector3(sgn4.X, sgn4.Y, sgn4.Z);
    }

    private static Vector3 GetLimit(IAxes axes, bool min)
    {
        if (min)
        {
            if (axes.Limit.Has_Min_Vector3Float())
            {
                return ToVector3(axes.Limit.Min_Vector3Float);
            }
            Vector4Float? min4 = axes.Limit.Min_Vector4Float;
            return min4 is null ? Vector3.Zero : new Vector3(min4.X, min4.Y, min4.Z);
        }
        if (axes.Limit.Has_Max_Vector3Float())
        {
            return ToVector3(axes.Limit.Max_Vector3Float);
        }
        Vector4Float? max4 = axes.Limit.Max_Vector4Float;
        return max4 is null ? Vector3.Zero : new Vector3(max4.X, max4.Y, max4.Z);
    }

    private static Vector3 ToVector3(IVector3Float? v) => v is null ? Vector3.Zero : new Vector3(v.X, v.Y, v.Z);

    private static Vector3 ToXformTranslation(IXform xform)
    {
        return xform.Has_T3() ? ToVector3(xform.T3) : new Vector3(xform.T4!.X, xform.T4.Y, xform.T4.Z);
    }

    // ── document-tree factory ────────────────────────────────────────────────

    /// <summary>
    /// The same numbers out of the avatar's serialized document tree (Unity's own m_* field names,
    /// JSON-encoded): what a Blender armature carries as its <c>ruri_unity_avatar</c> stamp. Field
    /// variants a version bump moves under us are tolerated the same way the typed side does:
    /// OffsetPtr <c>{data: ...}</c> wrappers peel, int arrays may arrive as little-endian hex
    /// strings, Sgn/Limit may carry three or four components, and numbers may arrive as strings.
    /// Returns null when the document has no human rig at all (m_Human absent/empty).
    /// </summary>
    public static AvatarRigInput? FromDocumentJson(string avatarDocumentJson)
    {
        JsonNode? root = JsonNode.Parse(avatarDocumentJson);
        JsonNode? constant = Unwrap(root?["m_Avatar"]);
        JsonNode? human = Unwrap(constant?["m_Human"]);
        JsonNode? skeleton = Unwrap(human?["m_Skeleton"]);
        if (skeleton?["m_Node"] is not JsonArray nodes || nodes.Count == 0)
        {
            return null;
        }

        int[] nodeParent = new int[nodes.Count];
        int[] nodeAxesId = new int[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            nodeParent[i] = GetInt(nodes[i]?["m_ParentId"], -1);
            nodeAxesId[i] = GetInt(nodes[i]?["m_AxesId"], -1);
        }

        int[] rawIds = IntArray(skeleton["m_ID"]);
        uint[] nodeId = new uint[rawIds.Length];
        for (int i = 0; i < rawIds.Length; i++)
        {
            nodeId[i] = unchecked((uint)rawIds[i]);
        }

        AxesRow[] axes = skeleton["m_AxesArray"] is JsonArray axesArray
            ? axesArray.Select(ReadAxes).ToArray()
            : [];

        JsonNode? skeletonPose = Unwrap(human?["m_SkeletonPose"]);
        (Vector3, Quaternion)[] pose = skeletonPose?["m_X"] is JsonArray xforms
            ? xforms.Select(x => (GetVector3(x?["t"]), GetQuaternion(x?["q"]))).ToArray()
            : [];

        JsonNode? rootX = human?["m_RootX"];

        return new AvatarRigInput
        {
            NodeParent = nodeParent,
            NodeAxesId = nodeAxesId,
            NodeId = nodeId,
            Axes = axes,
            Tos = ParseTos(root?["m_TOS"]),
            HumanBoneIndex = IntArray(human?["m_HumanBoneIndex"]),
            LeftHandBoneIndex = IntArray(Unwrap(human?["m_LeftHand"])?["m_HandBoneIndex"]),
            RightHandBoneIndex = IntArray(Unwrap(human?["m_RightHand"])?["m_HandBoneIndex"]),
            HumanBoneMass = FloatArray(human?["m_HumanBoneMass"]),
            RootRestQ = rootX is null ? Quaternion.Identity : GetQuaternion(rootX["q"]),
            ArmTwist = GetFloat(human?["m_ArmTwist"], 0.5f),
            ForeArmTwist = GetFloat(human?["m_ForeArmTwist"], 0.5f),
            UpperLegTwist = GetFloat(human?["m_UpperLegTwist"], 0.5f),
            LegTwist = GetFloat(human?["m_LegTwist"], 0.5f),
            SkeletonPose = pose,
        };
    }

    private static AxesRow ReadAxes(JsonNode? entry)
    {
        JsonNode? limit = entry?["m_Limit"];
        return new AxesRow(
            GetQuaternion(entry?["m_PreQ"]),
            GetQuaternion(entry?["m_PostQ"]),
            GetVector3(entry?["m_Sgn"], Vector3.One),
            GetVector3(limit?["m_Min"]),
            GetVector3(limit?["m_Max"]));
    }

    /// <summary>Peel Unity's OffsetPtr <c>{data: ...}</c> indirection.</summary>
    private static JsonNode? Unwrap(JsonNode? node)
    {
        while (node is JsonObject obj && obj.Count == 1 && obj.ContainsKey("data"))
        {
            node = obj["data"];
        }
        return node;
    }

    /// <summary>m_TOS as {hash: path}: a list of {first, second} pairs, a {key: path} flow-map
    /// entry list, or a plain dict -- the same three shapes the YAML side produces.</summary>
    private static Dictionary<uint, string> ParseTos(JsonNode? tos)
    {
        Dictionary<uint, string> result = new();
        if (tos is JsonArray pairs)
        {
            foreach (JsonNode? pair in pairs)
            {
                if (pair is not JsonObject obj)
                {
                    continue;
                }
                JsonNode? key = obj["first"];
                JsonNode? value = obj["second"];
                if (value is null && obj.Count == 1)
                {
                    foreach (var kv in obj)
                    {
                        AddTos(result, kv.Key, kv.Value?.ToString());
                    }
                    continue;
                }
                AddTos(result, key?.ToString(), value?.ToString());
            }
        }
        else if (tos is JsonObject map)
        {
            foreach (var kv in map)
            {
                AddTos(result, kv.Key, kv.Value?.ToString());
            }
        }
        return result;
    }

    private static void AddTos(Dictionary<uint, string> result, string? key, string? path)
    {
        if (string.IsNullOrEmpty(path)
            || !long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long hash))
        {
            return;
        }
        result[unchecked((uint)hash)] = path;
    }

    private static readonly char[] HexDigits = "0123456789abcdefABCDEF".ToCharArray();

    /// <summary>An int32 array that may arrive as a JSON list or as AssetRipper's little-endian
    /// hex string (tolerant of the trailing -1 padding run: parsing stops at the first non-hex
    /// 8-char chunk, everything needed precedes the padding).</summary>
    private static int[] IntArray(JsonNode? node)
    {
        if (node is JsonArray list)
        {
            return list.Select(item => GetInt(item, 0)).ToArray();
        }
        string text = node?.ToString() ?? string.Empty;
        List<int> result = new(text.Length / 8);
        for (int i = 0; i + 8 <= text.Length; i += 8)
        {
            ReadOnlySpan<char> chunk = text.AsSpan(i, 8);
            bool hex = true;
            foreach (char c in chunk)
            {
                if (Array.IndexOf(HexDigits, c) < 0)
                {
                    hex = false;
                    break;
                }
            }
            if (!hex)
            {
                break;
            }
            uint big = uint.Parse(chunk, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            result.Add(unchecked((int)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(big)));
        }
        return result.ToArray();
    }

    private static float[] FloatArray(JsonNode? node)
    {
        if (node is JsonArray list)
        {
            return list.Select(item => GetFloat(item, 0f)).ToArray();
        }
        int[] bits = IntArray(node);
        float[] result = new float[bits.Length];
        for (int i = 0; i < bits.Length; i++)
        {
            result[i] = BitConverter.Int32BitsToSingle(bits[i]);
        }
        return result;
    }

    private static int GetInt(JsonNode? node, int fallback)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int direct))
            {
                return direct;
            }
            if (value.TryGetValue(out long wide))
            {
                return unchecked((int)wide);
            }
            if (value.TryGetValue(out double real))
            {
                return (int)real;
            }
            if (value.TryGetValue(out string? text)
                && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                return unchecked((int)parsed);
            }
        }
        return fallback;
    }

    private static float GetFloat(JsonNode? node, float fallback)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out double real))
            {
                return (float)real;
            }
            if (value.TryGetValue(out int direct))
            {
                return direct;
            }
            if (value.TryGetValue(out string? text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                return (float)parsed;
            }
        }
        return fallback;
    }

    private static Vector3 GetVector3(JsonNode? node) => GetVector3(node, Vector3.Zero);

    private static Vector3 GetVector3(JsonNode? node, Vector3 fallback)
    {
        if (node is null)
        {
            return fallback;
        }
        return new Vector3(GetFloat(node["x"], 0f), GetFloat(node["y"], 0f), GetFloat(node["z"], 0f));
    }

    private static Quaternion GetQuaternion(JsonNode? node)
    {
        if (node is null)
        {
            return Quaternion.Identity;
        }
        return new Quaternion(GetFloat(node["x"], 0f), GetFloat(node["y"], 0f),
            GetFloat(node["z"], 0f), GetFloat(node["w"], 1f));
    }
}
