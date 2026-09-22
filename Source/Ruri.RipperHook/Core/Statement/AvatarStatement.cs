using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ruri.RipperHook.Humanoid;

namespace Ruri.RipperHook.Statements;

/// <summary>The form of an avatar the humanoid solver itself consumes, as text a host stamps
/// on the rig it built and hands back when a muscle-encoded clip is solved against it.
/// Produced straight off the Avatar asset; nothing passes through the engine's text form.</summary>
public static class AvatarStatement
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        IncludeFields = false,
    };

    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public int[] NodeParent { get; set; } = [];
        public int[] NodeAxesId { get; set; } = [];
        public uint[] NodeId { get; set; } = [];
        public float[][] Axes { get; set; } = [];
        public Dictionary<string, string> Tos { get; set; } = [];
        public int[] HumanBoneIndex { get; set; } = [];
        public int[] LeftHandBoneIndex { get; set; } = [];
        public int[] RightHandBoneIndex { get; set; } = [];
        public float[] HumanBoneMass { get; set; } = [];
        public float[] RootRestQ { get; set; } = [];
        public float ArmTwist { get; set; }
        public float ForeArmTwist { get; set; }
        public float UpperLegTwist { get; set; }
        public float LegTwist { get; set; }
        public float[][] SkeletonPose { get; set; } = [];
    }

    public static string ToJson(AvatarRigInput input)
    {
        Document document = new()
        {
            NodeParent = input.NodeParent,
            NodeAxesId = input.NodeAxesId,
            NodeId = input.NodeId,
            Axes = input.Axes.Select(axes => new[]
            {
                axes.PreQ.X, axes.PreQ.Y, axes.PreQ.Z, axes.PreQ.W,
                axes.PostQ.X, axes.PostQ.Y, axes.PostQ.Z, axes.PostQ.W,
                axes.Sgn.X, axes.Sgn.Y, axes.Sgn.Z,
                axes.LimitMin.X, axes.LimitMin.Y, axes.LimitMin.Z,
                axes.LimitMax.X, axes.LimitMax.Y, axes.LimitMax.Z,
            }).ToArray(),
            Tos = input.Tos.ToDictionary(pair => pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), pair => pair.Value),
            HumanBoneIndex = input.HumanBoneIndex,
            LeftHandBoneIndex = input.LeftHandBoneIndex,
            RightHandBoneIndex = input.RightHandBoneIndex,
            HumanBoneMass = input.HumanBoneMass,
            RootRestQ = [input.RootRestQ.X, input.RootRestQ.Y, input.RootRestQ.Z, input.RootRestQ.W],
            ArmTwist = input.ArmTwist,
            ForeArmTwist = input.ForeArmTwist,
            UpperLegTwist = input.UpperLegTwist,
            LegTwist = input.LegTwist,
            SkeletonPose = input.SkeletonPose.Select(pose => new[]
            {
                pose.T.X, pose.T.Y, pose.T.Z, pose.Q.X, pose.Q.Y, pose.Q.Z, pose.Q.W,
            }).ToArray(),
        };
        return JsonSerializer.Serialize(document, Options);
    }

    public static AvatarRigInput FromJson(string json)
    {
        Document document = JsonSerializer.Deserialize<Document>(json, Options)
            ?? throw new InvalidDataException("avatar statement deserialised to nothing.");
        if (document.Version != 1)
        {
            throw new InvalidDataException($"avatar statement version {document.Version} is not the one this build reads (1).");
        }
        Dictionary<uint, string> tos = new(document.Tos.Count);
        foreach ((string key, string path) in document.Tos)
        {
            tos[uint.Parse(key, System.Globalization.CultureInfo.InvariantCulture)] = path;
        }
        return new AvatarRigInput
        {
            NodeParent = document.NodeParent,
            NodeAxesId = document.NodeAxesId,
            NodeId = document.NodeId,
            Axes = document.Axes.Select(values => new AvatarRigInput.AxesRow(
                new Quaternion(values[0], values[1], values[2], values[3]),
                new Quaternion(values[4], values[5], values[6], values[7]),
                new Vector3(values[8], values[9], values[10]),
                new Vector3(values[11], values[12], values[13]),
                new Vector3(values[14], values[15], values[16]))).ToArray(),
            Tos = tos,
            HumanBoneIndex = document.HumanBoneIndex,
            LeftHandBoneIndex = document.LeftHandBoneIndex,
            RightHandBoneIndex = document.RightHandBoneIndex,
            HumanBoneMass = document.HumanBoneMass,
            RootRestQ = document.RootRestQ.Length == 4
                ? new Quaternion(document.RootRestQ[0], document.RootRestQ[1], document.RootRestQ[2], document.RootRestQ[3])
                : Quaternion.Identity,
            ArmTwist = document.ArmTwist,
            ForeArmTwist = document.ForeArmTwist,
            UpperLegTwist = document.UpperLegTwist,
            LegTwist = document.LegTwist,
            SkeletonPose = document.SkeletonPose.Select(values =>
                (new Vector3(values[0], values[1], values[2]),
                    new Quaternion(values[3], values[4], values[5], values[6]))).ToArray(),
        };
    }
}
