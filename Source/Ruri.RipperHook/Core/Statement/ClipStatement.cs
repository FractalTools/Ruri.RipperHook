using System.Runtime.InteropServices;
using System.Text.Json;
using Ruri.RipperHook.Animation;
using Ruri.RipperHook.Bridge;
using Ruri.RipperHook.Humanoid;

namespace Ruri.RipperHook.Statements;

/// <summary>
/// A clip's curves re-anchored onto the skeleton a host holds, and its muscle encoding
/// solved into ordinary bone curves against that skeleton's avatar. The blob layout is the
/// one every clip producer writes -- per curve, times then values then both tangents -- so a
/// reader cannot tell a repaired clip from a raw one.
/// </summary>
public static class ClipStatement
{
    private sealed record CurveEntry(string kind, string path, string? attr, int classId, int keys, long off);

    private sealed record ClipIndex(string name, float sampleRate, float startTime, float stopTime,
        bool keepPositionXZ, bool keepPositionY, bool keepOrientation, List<CurveEntry> curves);

    private sealed class Channel
    {
        public required string Kind { get; init; }
        public required string Path { get; set; }
        public string? Attribute { get; init; }
        public int ClassId { get; init; }
        public int Keys { get; init; }
        public required float[] Data { get; init; }
    }

    private static int Dimensions(string kind) => kind switch
    {
        "rot" => 4,
        "pos" or "scale" or "euler" => 3,
        _ => 1,
    };

    private static (ClipIndex Meta, List<Channel> Channels) Parse(string metaJson, byte[] payload)
    {
        ClipIndex meta = JsonSerializer.Deserialize<ClipIndex>(metaJson)
            ?? throw new InvalidDataException("clip blob meta deserialised to nothing.");
        ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(payload);
        List<Channel> channels = new(meta.curves.Count);
        foreach (CurveEntry entry in meta.curves)
        {
            int length = entry.keys + 3 * entry.keys * Dimensions(entry.kind);
            channels.Add(new Channel
            {
                Kind = entry.kind,
                Path = entry.path,
                Attribute = entry.attr,
                ClassId = entry.classId,
                Keys = entry.keys,
                Data = floats.Slice(checked((int)entry.off), length).ToArray(),
            });
        }
        return (meta, channels);
    }

    private static (string MetaJson, byte[] Curves) Write(ClipIndex source, List<Channel> channels)
    {
        List<CurveEntry> index = new(channels.Count);
        long total = 0;
        foreach (Channel channel in channels)
        {
            index.Add(new CurveEntry(channel.Kind, channel.Path, channel.Attribute, channel.ClassId, channel.Keys, total));
            total += channel.Data.Length;
        }
        float[] payload = new float[total];
        int cursor = 0;
        foreach (Channel channel in channels)
        {
            channel.Data.CopyTo(payload, cursor);
            cursor += channel.Data.Length;
        }
        ClipIndex meta = new(source.name, source.sampleRate, source.startTime, source.stopTime,
            source.keepPositionXZ, source.keepPositionY, source.keepOrientation, index);
        return (JsonSerializer.Serialize(meta), MemoryMarshal.AsBytes<float>(payload).ToArray());
    }

    /// <summary>Rewrite curve paths onto the skeleton's own full paths through the suffix-CRC
    /// join: a hashed placeholder resolves by its literal hash, a restored path that is not
    /// literally a skeleton path resolves by hashing, a verbatim match is left alone.</summary>
    private static (int Repaired, int Unmatched) Repair(List<Channel> channels, IReadOnlySet<string> skeletonPaths,
        Dictionary<uint, string> suffixes)
    {
        int repaired = 0;
        int unmatched = 0;
        foreach (Channel channel in channels)
        {
            if (channel.Path.Length == 0 || skeletonPaths.Contains(channel.Path))
            {
                continue;
            }
            if (suffixes.TryGetValue(UnitySkinning.EntryCrc(channel.Path), out string? real))
            {
                channel.Path = real;
                repaired++;
            }
            else
            {
                unmatched++;
            }
        }
        return (repaired, unmatched);
    }

    /// <summary>The clip as the given skeleton plays it. <paramref name="skeletonPaths"/>
    /// re-anchors every curve; <paramref name="avatarJson"/>, when stated, solves a
    /// muscle-encoded clip into bone curves that replace whatever rode on those paths.</summary>
    public static StatementClip Restate(StatementClip clip, string skeletonKey, IReadOnlyList<string> skeletonPaths,
        string avatarJson, Action<string> note)
    {
        if (skeletonPaths.Count == 0 && avatarJson.Length == 0)
        {
            return clip;
        }
        (ClipIndex meta, List<Channel> channels) = Parse(clip.MetaJson, clip.Curves);
        HashSet<string> paths = new(skeletonPaths, StringComparer.Ordinal);
        Dictionary<uint, string> suffixes = UnitySkinning.SuffixTable(skeletonPaths);
        if (skeletonPaths.Count > 0)
        {
            (int repaired, int unmatched) = Repair(channels, paths, suffixes);
            if (unmatched > 0)
            {
                note($"{clip.Name}: {unmatched} curve path(s) matched no bone of the target skeleton ({repaired} re-anchored)");
            }
        }
        if (avatarJson.Length > 0)
        {
            Solve(clip, meta, channels, paths, suffixes, avatarJson, note);
        }
        (string metaJson, byte[] curves) = Write(meta, channels);
        return new StatementClip(clip.Key, clip.Name, skeletonKey, metaJson, curves);
    }

    private static void Solve(StatementClip clip, ClipIndex meta, List<Channel> channels, HashSet<string> paths,
        Dictionary<uint, string> suffixes, string avatarJson, Action<string> note)
    {
        (List<(string Attribute, HermiteCurve Curve)> floatChannels, float sampleRate, bool keepXZ, bool keepY,
            bool keepOrientation) = ClipCurveBlob.ReadFloatChannels(clip.MetaJson, clip.Curves);
        if (!HumanoidClipGenericizer.HasMuscleChannel(floatChannels))
        {
            return;
        }
        AvatarMuscleReferential? referential = AvatarMuscleReferential.TryCreate(AvatarStatement.FromJson(avatarJson));
        if (referential is null)
        {
            note($"{clip.Name}: the avatar carries no human rig, so its muscle curves stay unsolved");
            return;
        }
        SolvedHumanoidPose? pose = HumanoidClipGenericizer.Solve(referential, floatChannels, sampleRate, keepXZ, keepY, keepOrientation);
        if (pose is null)
        {
            return;
        }
        string[] consumed = floatChannels
            .Select(channel => channel.Attribute)
            .Where(attribute => AvatarMuscleReferential.IsMuscleAttribute(attribute) || AvatarMuscleReferential.IsRootAttribute(attribute))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        (string solvedMeta, byte[] solvedCurves) = ClipCurveBlob.BuildSolved(pose, consumed, keepXZ, keepY, keepOrientation);
        (_, List<Channel> solved) = Parse(solvedMeta, solvedCurves);
        (_, int unmatched) = Repair(solved, paths, suffixes);
        if (unmatched > 0)
        {
            note($"{clip.Name}: {unmatched} solved humanoid bone path(s) matched no bone of the target skeleton");
        }
        HashSet<string> consumedSet = new(consumed, StringComparer.Ordinal);
        channels.RemoveAll(channel => channel.Kind == "float" && channel.Attribute is not null && consumedSet.Contains(channel.Attribute));
        foreach (string kind in new[] { "rot", "pos" })
        {
            HashSet<string> solvedPaths = new(solved.Where(channel => channel.Kind == kind).Select(channel => channel.Path), StringComparer.Ordinal);
            channels.RemoveAll(channel => channel.Kind == kind && solvedPaths.Contains(channel.Path));
        }
        channels.AddRange(solved.Where(channel => channel.Kind is "rot" or "pos"));
    }
}
