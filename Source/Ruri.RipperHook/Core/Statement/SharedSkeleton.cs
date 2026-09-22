using System.Globalization;
using System.Numerics;

namespace Ruri.RipperHook.Statements;

/// <summary>
/// One rig for an assembled character, posed from the template skeleton's OWN standing rest
/// rather than from the part meshes' bind poses. A part mesh arrives on its own, in its
/// authoring space, with a bind pose and a CRC32 name hash per bone but no rig; the template
/// avatar brings the crc32(path) to path table and the whole skeleton's standing world rest,
/// and every part mesh is bind-baked against it. The first mesh builds the rig, later parts
/// extend it; a part authored at its own origin is aligned by the shared bone it hangs under;
/// a hash the table does not name is reconstructed from leaf names, or kept hash-named.
/// </summary>
public sealed class SharedSkeleton
{
    public sealed class Bone
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required uint Hash { get; init; }
        public required double[] World { get; init; }
        public string Parent { get; set; } = string.Empty;
    }

    private readonly List<string> _leaves;
    private readonly Dictionary<uint, double[]> _worldRest = [];
    private readonly Dictionary<uint, string> _pathByHash = [];
    private readonly Dictionary<string, double[]> _worldByName = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _hashToBone = [];
    private readonly Dictionary<string, Bone> _bonesByName = new(StringComparer.Ordinal);

    public List<Bone> Bones { get; } = [];

    public SharedSkeleton(IEnumerable<string> leafNames)
    {
        _leaves = leafNames.Where(static name => name.Length > 0).ToList();
    }

    public IReadOnlyDictionary<uint, string> PathByHash => _pathByHash;

    /// <summary>Merge one avatar's skeleton into the rig, aligned onto what is already there;
    /// the first rest a bone is stated at is the authoritative one.</summary>
    public void AddSkeleton(AvatarSkeleton skeleton)
    {
        Dictionary<uint, string> nameByHash = [];
        foreach (string path in skeleton.Paths)
        {
            if (path.Length == 0)
            {
                continue;
            }
            uint key = UnitySkinning.Crc(path);
            _pathByHash.TryAdd(key, path);
            nameByHash.TryAdd(key, Leaf(path));
        }
        double[] offset = Alignment(skeleton.WorldRests, nameByHash);
        foreach ((uint key, double[] rest) in skeleton.WorldRests)
        {
            double[] aligned = Mat4.Multiply(offset, rest);
            _worldRest.TryAdd(key, aligned);
            if (nameByHash.TryGetValue(key, out string? name))
            {
                _worldByName.TryAdd(name, aligned);
            }
        }
    }

    /// <summary>The 4x4 that lands this part's attachment bone -- the shared bone most of its
    /// own bones hang under -- onto the standing copy already on the rig.</summary>
    private double[] Alignment(IReadOnlyDictionary<uint, double[]> worldRests, Dictionary<uint, string> nameByHash)
    {
        if (_worldByName.Count == 0)
        {
            return Mat4.Identity();
        }
        Dictionary<string, double[]> partByName = new(StringComparer.Ordinal);
        foreach (uint key in worldRests.Keys)
        {
            if (nameByHash.TryGetValue(key, out string? name))
            {
                partByName.TryAdd(name, worldRests[key]);
            }
        }
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (uint key in worldRests.Keys)
        {
            if (!nameByHash.TryGetValue(key, out string? name) || !_pathByHash.TryGetValue(key, out string? path)
                || _worldByName.ContainsKey(name))
            {
                continue;
            }
            string[] segments = path.Split('/');
            for (int cut = segments.Length - 1; cut > 0; cut--)
            {
                string ancestor = segments[cut - 1];
                if (_worldByName.ContainsKey(ancestor) && partByName.ContainsKey(ancestor))
                {
                    counts[ancestor] = counts.GetValueOrDefault(ancestor) + 1;
                    break;
                }
            }
        }
        if (counts.Count == 0)
        {
            return Mat4.Identity();
        }
        string attach = counts.MaxBy(pair => pair.Value).Key;
        double[]? inverse = Invert(partByName[attach]);
        return inverse is null ? Mat4.Identity() : Mat4.Multiply(_worldByName[attach], inverse);
    }

    /// <summary>Bind-bake a mesh onto the standing rest and grow the rig by every bone it
    /// references and their structural ancestors. The bone paths come back per skin slot.
    /// False when the mesh carries no usable bind data.</summary>
    public bool Bind(DecodedMesh decoded, out List<string> bonePaths)
    {
        bonePaths = [];
        uint[]? hashes = decoded.BoneNameHashes;
        if (hashes is null || hashes.Length == 0 || decoded.BindPoses is null || decoded.BindPoseCount != hashes.Length)
        {
            return false;
        }
        List<uint> unresolved = hashes.Where(key => !_pathByHash.ContainsKey(key)).ToList();
        if (unresolved.Count > 0)
        {
            foreach ((uint key, string path) in UnitySkinning.ResolveBonePaths(unresolved, _leaves, _pathByHash.Values.ToList()))
            {
                _pathByHash[key] = path;
            }
        }
        List<double[]?> worlds = new(hashes.Length);
        foreach (uint key in hashes)
        {
            worlds.Add(_worldRest.GetValueOrDefault(key));
        }
        bool baked = UnitySkinning.BakeBindPose(decoded, worlds);
        Grow(hashes);
        foreach (uint key in hashes)
        {
            bonePaths.Add(_hashToBone.TryGetValue(key, out string? bone) ? PathOfBone(bone) : BoneNameFor(null, key));
        }
        return baked;
    }

    private string PathOfBone(string bone) => _bonesByName.TryGetValue(bone, out Bone? found) ? found.Path : bone;

    private void Grow(uint[] meshKeys)
    {
        List<uint> rooted = meshKeys.Where(_worldRest.ContainsKey).ToList();
        HashSet<uint> needed = WithAncestors(rooted);
        List<uint> fresh = needed.Where(key => !_hashToBone.ContainsKey(key) && _worldRest.ContainsKey(key)).ToList();
        if (fresh.Count == 0)
        {
            return;
        }
        fresh.Sort((left, right) => Depth(left).CompareTo(Depth(right)));
        List<Bone> created = [];
        foreach (uint key in fresh)
        {
            string? path = _pathByHash.GetValueOrDefault(key);
            string boneName = BoneNameFor(path, key);
            if (_bonesByName.TryGetValue(boneName, out Bone? existing))
            {
                _hashToBone[key] = existing.Name;
                continue;
            }
            Bone bone = new() { Name = boneName, Path = path ?? boneName, Hash = key, World = _worldRest[key] };
            _bonesByName[boneName] = bone;
            Bones.Add(bone);
            _hashToBone[key] = boneName;
            created.Add(bone);
        }
        foreach (Bone bone in created)
        {
            if (_pathByHash.TryGetValue(bone.Hash, out string? path))
            {
                bone.Parent = ParentBone(path) ?? string.Empty;
            }
        }
    }

    private HashSet<uint> WithAncestors(List<uint> keys)
    {
        HashSet<uint> needed = new(keys);
        foreach (uint key in keys)
        {
            if (!_pathByHash.TryGetValue(key, out string? path))
            {
                continue;
            }
            string[] segments = path.Split('/');
            for (int cut = 1; cut < segments.Length; cut++)
            {
                string ancestor = string.Join('/', segments, 0, cut);
                uint ancestorKey = UnitySkinning.Crc(ancestor);
                _pathByHash.TryAdd(ancestorKey, ancestor);
                needed.Add(ancestorKey);
            }
        }
        return needed;
    }

    private int Depth(uint key) => _pathByHash.TryGetValue(key, out string? path) ? path.Count(character => character == '/') : 0;

    private string? ParentBone(string path)
    {
        string[] segments = path.Split('/');
        for (int cut = segments.Length - 1; cut > 0; cut--)
        {
            if (_hashToBone.TryGetValue(UnitySkinning.Crc(string.Join('/', segments, 0, cut)), out string? bone))
            {
                return bone;
            }
        }
        return null;
    }

    /// <summary>A bone's rest relative to its parent ON THIS RIG -- what a clip's local TRS is
    /// relative to; the alignment a part was merged under cancels out of it.</summary>
    public (Vector3 Position, Quaternion Rotation, Vector3 Scale) LocalRest(Bone bone)
    {
        double[] local = bone.World;
        if (bone.Parent.Length > 0 && _bonesByName.TryGetValue(bone.Parent, out Bone? parent))
        {
            double[]? inverse = Invert(parent.World);
            if (inverse is not null)
            {
                local = Mat4.Multiply(inverse, bone.World);
            }
        }
        return Mat4.Decompose(local);
    }

    public static string BoneNameFor(string? path, uint key) =>
        path is { Length: > 0 } ? Leaf(path) : "bone_" + key.ToString("x8", CultureInfo.InvariantCulture);

    private static string Leaf(string path)
    {
        int cut = path.LastIndexOf('/');
        return cut < 0 ? path : path[(cut + 1)..];
    }

    private static double[]? Invert(double[] matrix) => Mat4.Invert(matrix);
}
