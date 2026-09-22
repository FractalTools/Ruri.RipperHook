using AssetRipper.Checksum;
using System.Text.RegularExpressions;

namespace Ruri.RipperHook.Statements;

/// <summary>
/// Bind-pose baking and bone-path identity for skinned meshes.
///
/// A SkinnedMeshRenderer's vertices are stored in the mesh's own space and reach their
/// visible positions once the skin transform is applied; the pose the renderer displays at
/// rest, in the prefab's space, is what a host places its rig at and the only correct pose
/// to texture on. A mesh identifies its bones by the CRC32 of each bone's whole root-relative
/// transform path, so a skeleton listed by leaf names is re-joined by hashing every candidate
/// path and every path suffix -- exact path identity, re-anchored, never a display-name guess.
/// </summary>
public static partial class UnitySkinning
{
    [GeneratedRegex("^path_0x([0-9A-Fa-f]{1,8})_")]
    private static partial Regex HashedPath();

    public static uint Crc(string path) => Crc32Algorithm.HashUTF8(path);

    /// <summary>The Unity transform paths a set of bone-name hashes stands for, grown from
    /// leaf names: every still-unknown hash is tested against known-path/leaf, and each hit
    /// is itself a parent for the next round. Seeds are full paths already known.</summary>
    public static Dictionary<uint, string> ResolveBonePaths(IEnumerable<uint> targetHashes,
        IEnumerable<string> leafNames, IEnumerable<string> seedPaths)
    {
        HashSet<uint> targets = new(targetHashes);
        List<string> leaves = leafNames.Where(static name => name.Length > 0).ToList();
        Dictionary<uint, string> resolved = [];

        bool Consider(string path)
        {
            uint key = Crc(path);
            if (targets.Contains(key) && !resolved.ContainsKey(key))
            {
                resolved[key] = path;
                return true;
            }
            return false;
        }

        List<string> frontier = seedPaths.ToList();
        foreach (string path in frontier.ToArray())
        {
            Consider(path);
        }
        foreach (string leaf in leaves)
        {
            if (Consider(leaf))
            {
                frontier.Add(leaf);
            }
        }
        while (frontier.Count > 0 && resolved.Count < targets.Count)
        {
            List<string> discovered = [];
            foreach (string parent in frontier)
            {
                foreach (string leaf in leaves)
                {
                    string candidate = parent + "/" + leaf;
                    if (Consider(candidate))
                    {
                        discovered.Add(candidate);
                    }
                }
            }
            frontier = discovered;
        }
        return resolved;
    }

    /// <summary>CRC32 of every level-suffix of every skeleton path -> the full path, so a
    /// clip anchored at any depth inside the hierarchy joins. On a collision the longest
    /// suffix wins. The animator root's empty path is excluded: crc32("") is 0, which a
    /// "path_0x0_" placeholder would wrongly resolve to.</summary>
    public static Dictionary<uint, string> SuffixTable(IEnumerable<string> paths)
    {
        Dictionary<uint, (int Length, string Path)> table = [];
        foreach (string path in paths)
        {
            if (path == UnityNode.AnimatorRootPath)
            {
                continue;
            }
            string[] parts = path.Split('/');
            for (int start = 0; start < parts.Length; start++)
            {
                string suffix = string.Join('/', parts, start, parts.Length - start);
                uint crc = Crc(suffix);
                if (!table.TryGetValue(crc, out (int Length, string Path) previous) || suffix.Length > previous.Length)
                {
                    table[crc] = (suffix.Length, path);
                }
            }
        }
        Dictionary<uint, string> resolved = new(table.Count);
        foreach ((uint crc, (_, string path)) in table)
        {
            resolved[crc] = path;
        }
        return resolved;
    }

    /// <summary>The binding CRC32 a curve path stands for: a hashed placeholder carries it
    /// literally, a restored string path hashes to it.</summary>
    public static uint EntryCrc(string path)
    {
        Match match = HashedPath().Match(path);
        return match.Success ? Convert.ToUInt32(match.Groups[1].Value, 16) : Crc(path);
    }

    /// <summary>Bake mesh-local vertices into their bind-pose positions in the prefab's
    /// space: bind(v) = sum_j w_j (boneWorld_j bindpose_j) v. Normals travel by the inverse
    /// transpose of the linear part, tangents by the linear part with their handedness kept.
    /// False when the mesh carries no skin, in which case the caller places it by its node.</summary>
    public static bool BakeBindPose(DecodedMesh decoded, IReadOnlyList<double[]?> boneWorlds)
    {
        if (decoded.BindPoses is null || decoded.BoneWeights is null || decoded.BoneIndices is null
            || decoded.Positions is null || boneWorlds.Count == 0)
        {
            return false;
        }
        int boneCount = boneWorlds.Count;
        int bindCount = Math.Min(boneCount, decoded.BindPoseCount);
        double[][] skin = new double[boneCount][];
        for (int slot = 0; slot < boneCount; slot++)
        {
            if (slot < bindCount)
            {
                double[] world = boneWorlds[slot] ?? Mat4.Identity();
                double[] bind = Mat4.FromFloats(decoded.BindPoses.AsSpan(slot * 16, 16));
                skin[slot] = Mat4.Multiply(world, bind);
            }
            else
            {
                skin[slot] = Mat4.Identity();
            }
        }

        int vertexCount = decoded.Positions.Length / 3;
        int influences = decoded.InfluenceCount;
        float[] positions = decoded.Positions;
        float[] weights = decoded.BoneWeights;
        int[] indices = decoded.BoneIndices;
        float[] baked = new float[positions.Length];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            double x = positions[vertex * 3];
            double y = positions[vertex * 3 + 1];
            double z = positions[vertex * 3 + 2];
            double bx = 0, by = 0, bz = 0;
            for (int influence = 0; influence < influences; influence++)
            {
                int slot = Math.Clamp(indices[vertex * influences + influence], 0, boneCount - 1);
                double weight = weights[vertex * influences + influence];
                Mat4.TransformPoint(skin[slot], x, y, z, out double tx, out double ty, out double tz);
                bx += weight * tx;
                by += weight * ty;
                bz += weight * tz;
            }
            baked[vertex * 3] = (float)bx;
            baked[vertex * 3 + 1] = (float)by;
            baked[vertex * 3 + 2] = (float)bz;
        }
        decoded.Positions = baked;

        double[][] linear = new double[boneCount][];
        double[][] normalMatrices = new double[boneCount][];
        for (int slot = 0; slot < boneCount; slot++)
        {
            linear[slot] = Mat4.Linear3(skin[slot]);
            normalMatrices[slot] = Mat4.NormalMatrix(skin[slot]) ?? linear[slot];
        }
        if (decoded.Normals is not null)
        {
            decoded.Normals = Blend(decoded.Normals, 3, vertexCount, influences, weights, indices, normalMatrices, boneCount);
        }
        if (decoded.Tangents is not null)
        {
            float[] tangents = decoded.Tangents;
            float[] xyz = new float[vertexCount * 3];
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                xyz[vertex * 3] = tangents[vertex * 4];
                xyz[vertex * 3 + 1] = tangents[vertex * 4 + 1];
                xyz[vertex * 3 + 2] = tangents[vertex * 4 + 2];
            }
            float[] blended = Blend(xyz, 3, vertexCount, influences, weights, indices, linear, boneCount);
            float[] result = new float[vertexCount * 4];
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                result[vertex * 4] = blended[vertex * 3];
                result[vertex * 4 + 1] = blended[vertex * 3 + 1];
                result[vertex * 4 + 2] = blended[vertex * 3 + 2];
                result[vertex * 4 + 3] = tangents[vertex * 4 + 3];
            }
            decoded.Tangents = result;
        }
        return true;
    }

    private static float[] Blend(float[] vectors, int stride, int vertexCount, int influences, float[] weights,
        int[] indices, double[][] matrices, int boneCount)
    {
        float[] result = new float[vertexCount * stride];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            double x = vectors[vertex * stride];
            double y = vectors[vertex * stride + 1];
            double z = vectors[vertex * stride + 2];
            double bx = 0, by = 0, bz = 0;
            for (int influence = 0; influence < influences; influence++)
            {
                int slot = Math.Clamp(indices[vertex * influences + influence], 0, boneCount - 1);
                double weight = weights[vertex * influences + influence];
                Mat4.TransformDirection3(matrices[slot], x, y, z, out double tx, out double ty, out double tz);
                bx += weight * tx;
                by += weight * ty;
                bz += weight * tz;
            }
            double length = Math.Sqrt(bx * bx + by * by + bz * bz);
            if (length < 1e-6)
            {
                length = 1.0;
            }
            result[vertex * stride] = (float)(bx / length);
            result[vertex * stride + 1] = (float)(by / length);
            result[vertex * stride + 2] = (float)(bz / length);
        }
        return result;
    }
}
