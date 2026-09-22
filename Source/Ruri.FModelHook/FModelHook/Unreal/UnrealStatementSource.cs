using System.Numerics;
using System.Runtime.InteropServices;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Data;
using Ruri.RipperHook.Statements;
using Ruri.RipperHook.Tables;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// What one package of this engine states, as the one statement every host reads: the scene
/// components an actor or world places, the buffers of every mesh they draw, the reference
/// skeleton a skinned one indexes, the parameters each material resolves to and the pixels
/// each texture decodes to -- read off this engine's own datasets, which already state them
/// in Unity's basis, and restated in the requester's by the same tables everything else is.
/// </summary>
public static class UnrealStatementSource
{
    public const string PackagePrefix = "package:";
    private const string Separator = ";";
    private static readonly string[] TrsColumns = ["px", "py", "pz", "qx", "qy", "qz", "qw", "sx", "sy", "sz"];

    private static readonly StatementSources.Source Registered = Resolve;

    public static void Register() => StatementSources.Register(Registered);

    public static void Unregister() => StatementSources.Remove(Registered);

    /// <summary>Which way this build's characters face in their OWN space, handed over in the
    /// basis every other coordinate of this session crosses in -- one conversion, here, not one
    /// per host.
    ///
    /// A placed actor needs no such statement: its component transform turns the mesh to the
    /// actor's forward. A character imported as a bare skeletal mesh has no component, so the
    /// build itself is the only thing that can say which way that mesh faces.</summary>
    public static Vector3 CharacterForward(UnrealTitles.Claim? claim)
    {
        Vector3 declared = claim?.Title.CharacterForward ?? UnrealTitle.EngineForward;
        return UnrealBasis.Basis.Direction(declared.X, declared.Y, declared.Z);
    }

    private static StatementPlan? Resolve(string seed, CabTable map, StatementOptions options)
    {
        string package = seed.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase) ? seed[PackagePrefix.Length..] : seed;
        if (package.Length == 0 || !map.TryGetId(package, out _))
        {
            return null;
        }
        string label = package[(package.Replace('\\', '/').LastIndexOf('/') + 1)..];
        int dot = label.LastIndexOf('.');
        if (dot > 0)
        {
            label = label[..dot];
        }
        return new StatementPlan
        {
            Seed = seed,
            Label = label,
            Kind = StatementKind.Placements,
            Cabs = [package],
            Flatten = requested => Flatten(seed, package, label, map, requested),
        };
    }

    private sealed record LibraryEntry(DecodedMesh Geometry, IReadOnlyList<string> OwnMaterials, IReadOnlyList<string> Bones,
        List<(string Name, int Parent, Vector3 Position, Quaternion Rotation, Vector3 Scale, string Path)> Skeleton, string Key);

    private static Statement Flatten(string seed, string package, string label, CabTable map, StatementOptions options)
    {
        Statement statement = new();
        (_, ColumnTable rows) = Datasets.Table(UnrealDatasets.PlacementsId, [UnrealDatasets.PackageParam, package], default, map);
        if (rows.RowCount == 0)
        {
            statement.Note(seed, "package places nothing", 0, package);
            return statement;
        }
        TextureRoles roles = TextureRoles.Load(options.RoleLayers);
        Dictionary<string, LibraryEntry> library = Library(seed, statement, rows, map, options.Detail);
        Dictionary<string, string> materialKeys = Materials(seed, statement, rows, library, map, roles);

        Column name = rows["name"];
        Column parent = rows["parent"];
        Column active = rows["active"];
        Column mesh = rows["mesh"];
        Column materials = rows["materials"];
        Column light = rows["light"];
        Column[] trs = TrsColumns.Select(column => rows[column]).ToArray();
        Column[] lightColumns = new[] { "lr", "lg", "lb", "intensity", "range", "outer", "inner", "width", "height" }.Select(column => rows[column]).ToArray();
        statement.Roots.Add(new StatementRoot(seed, 0, label, "placements")
        {
            Forward = CharacterForward(UnrealTitles.Of(Session.GameRoot)),
        });
        Dictionary<int, string> skeletonOf = Skeletons(seed, statement, rows, library);
        for (int row = 0; row < rows.RowCount; row++)
        {
            string meshPath = mesh.Text(row);
            LibraryEntry? entry = meshPath.Length > 0 ? library.GetValueOrDefault(meshPath) : null;
            string kind = "empty";
            string skeletonKey = skeletonOf.GetValueOrDefault(row, string.Empty);
            List<string> slots = [];
            if (entry is not null)
            {
                string stated = materials.Text(row);
                IReadOnlyList<string> paths = stated.Length > 0 ? stated.Split(Separator) : entry.OwnMaterials;
                slots = paths.Select(path => materialKeys.GetValueOrDefault(path, string.Empty)).ToList();
                kind = skeletonKey.Length > 0 ? "skinned" : "mesh";
            }
            UnityLightInfo? lightInfo = null;
            string lightKind = light.Text(row);
            if (entry is null && lightKind.Length > 0)
            {
                kind = "light";
                lightInfo = new UnityLightInfo
                {
                    Node = null!,
                    Name = name.Text(row),
                    Type = LightType(lightKind),
                    Red = (float)lightColumns[0].Real(row),
                    Green = (float)lightColumns[1].Real(row),
                    Blue = (float)lightColumns[2].Real(row),
                    Intensity = (float)lightColumns[3].Real(row),
                    Range = (float)lightColumns[4].Real(row),
                    SpotAngle = (float)lightColumns[5].Real(row),
                    InnerSpotAngle = (float)lightColumns[6].Real(row),
                    AreaWidth = (float)lightColumns[7].Real(row),
                    AreaHeight = (float)lightColumns[8].Real(row),
                    Disabled = !active.Truthy(row),
                };
            }
            statement.Nodes.Add(new StatementNode
            {
                Index = row,
                Parent = (int)parent.Integer(row),
                Name = name.Text(row),
                Path = name.Text(row),
                Kind = kind,
                Active = active.Truthy(row),
                Mesh = entry?.Key ?? string.Empty,
                Skeleton = skeletonKey,
                Materials = slots,
                Position = new Vector3((float)trs[0].Real(row), (float)trs[1].Real(row), (float)trs[2].Real(row)),
                Rotation = new Quaternion((float)trs[3].Real(row), (float)trs[4].Real(row), (float)trs[5].Real(row), (float)trs[6].Real(row)),
                Scale = new Vector3((float)trs[7].Real(row), (float)trs[8].Real(row), (float)trs[9].Real(row)),
                Light = lightInfo,
            });
        }
        return statement;
    }

    /// <summary>Which skeleton each skinned row binds to: one per (actor, skeleton) rather than
    /// one per mesh, and the skeleton itself stated once.
    ///
    /// A character is not one mesh. A build wears a body, a head state and a hair on ONE
    /// skeleton, each its own component, and every one of them indexes the SAME reference
    /// skeleton -- so a skeleton per component is three copies of one rig with the character torn
    /// between them, which is not what the actor is. The actor is the boundary: components of one
    /// actor whose bones a single skeleton covers share it; a second actor's identical skeleton is
    /// a second character and gets its own. The row's own actor decides that, never its
    /// attachment -- a Blueprint's components can all sit at the top.
    ///
    /// The fullest skeleton in a group is the one stated, so a component naming fewer bones rides
    /// the complete one rather than forcing a second; ties go to the heaviest mesh, which is the
    /// body, so the skeleton reads as the character rather than as whichever component came
    /// first.</summary>
    private static Dictionary<int, string> Skeletons(string seed, Statement statement, ColumnTable rows,
        IReadOnlyDictionary<string, LibraryEntry> library)
    {
        Column mesh = rows["mesh"];
        Column actorColumn = rows["actor"];
        Dictionary<int, List<(int Row, LibraryEntry Entry)>> byActor = [];
        for (int row = 0; row < rows.RowCount; row++)
        {
            string path = mesh.Text(row);
            LibraryEntry? entry = path.Length > 0 ? library.GetValueOrDefault(path) : null;
            if (entry is null || entry.Skeleton.Count == 0)
            {
                continue;
            }
            int actor = (int)actorColumn.Integer(row);
            if (!byActor.TryGetValue(actor, out List<(int Row, LibraryEntry Entry)>? group))
            {
                byActor[actor] = group = [];
            }
            group.Add((row, entry));
        }
        Dictionary<int, string> found = [];
        foreach (int actor in byActor.Keys.Order())
        {
            List<(HashSet<string> Covered, string Key)> chosen = [];
            foreach ((int row, LibraryEntry entry) in byActor[actor]
                         .OrderByDescending(pair => pair.Entry.Skeleton.Count)
                         .ThenByDescending(pair => pair.Entry.Geometry.Positions?.Length ?? 0))
            {
                HashSet<string> stated = new(entry.Skeleton.Select(bone => bone.Name), StringComparer.Ordinal);
                string key = string.Empty;
                foreach ((HashSet<string> covered, string held) in chosen)
                {
                    if (stated.IsSubsetOf(covered))
                    {
                        key = held;
                        break;
                    }
                }
                if (key.Length == 0)
                {
                    key = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{seed}|{row}");
                    StatementSkeleton skeleton = new() { Key = key };
                    for (int bone = 0; bone < entry.Skeleton.Count; bone++)
                    {
                        (string boneName, int boneParent, Vector3 position, Quaternion rotation, Vector3 scale, string path) = entry.Skeleton[bone];
                        skeleton.Bones.Add(new StatementBone(bone, boneParent, boneName, path, path, position, rotation, scale, string.Empty));
                    }
                    statement.Skeletons.Add(skeleton);
                    chosen.Add((stated, key));
                }
                found[row] = key;
            }
        }
        return found;
    }

    private static int LightType(string kind) => kind.ToLowerInvariant() switch
    {
        "spot" => 0,
        "directional" => 1,
        "point" => 2,
        _ => 3,
    };

    /// <summary>One decode per distinct mesh, at the LOD nearest the wanted detail level.</summary>
    private static Dictionary<string, LibraryEntry> Library(string seed, Statement statement, ColumnTable rows, CabTable map, int detail)
    {
        Dictionary<string, LibraryEntry> library = new(StringComparer.Ordinal);
        Column meshColumn = rows["mesh"];
        SortedSet<string> paths = new(StringComparer.Ordinal);
        for (int row = 0; row < rows.RowCount; row++)
        {
            string path = meshColumn.Text(row);
            if (path.Length > 0)
            {
                paths.Add(path);
            }
        }
        int wanted = Math.Max(0, detail);
        foreach (string path in paths)
        {
            (_, ColumnTable geometry) = Datasets.Table(UnrealDatasets.MeshGeometryId, [UnrealDatasets.PackageParam, path], default, map);
            string export = path[(path.LastIndexOf('.') + 1)..];
            int chosen = -1;
            int chosenLevel = 0;
            Column nameColumn = geometry["name"];
            Column lodColumn = geometry["lod"];
            for (int row = 0; row < geometry.RowCount; row++)
            {
                if (nameColumn.Text(row) != export)
                {
                    continue;
                }
                int level = (int)lodColumn.Integer(row);
                if (chosen < 0 || Math.Abs(level - wanted) < Math.Abs(chosenLevel - wanted))
                {
                    chosen = row;
                    chosenLevel = level;
                }
            }
            if (chosen < 0)
            {
                statement.Note(seed, "placement mesh not in the package", 1, path);
                continue;
            }
            DecodedMesh decoded = Decode(geometry, chosen, export);
            string joinedBones = geometry["bones"].Text(chosen);
            List<string> bones = joinedBones.Length > 0 ? joinedBones.Split(Separator).ToList() : [];
            string joinedMaterials = geometry["materials"].Text(chosen);
            List<string> own = joinedMaterials.Length > 0 ? joinedMaterials.Split(Separator).ToList() : [];
            List<(string Name, int Parent, Vector3 Position, Quaternion Rotation, Vector3 Scale, string Path)> skeleton = [];
            if (bones.Count > 0)
            {
                (_, ColumnTable skeletonRows) = Datasets.Table(UnrealDatasets.MeshSkeletonId, [UnrealDatasets.PackageParam, path], default, map);
                Column meshOf = skeletonRows["mesh"];
                Column boneName = skeletonRows["bone"];
                Column boneParent = skeletonRows["parent"];
                Column bonePath = skeletonRows["path"];
                Column[] trs = TrsColumns.Select(column => skeletonRows[column]).ToArray();
                for (int row = 0; row < skeletonRows.RowCount; row++)
                {
                    if (meshOf.Text(row) != export)
                    {
                        continue;
                    }
                    skeleton.Add((boneName.Text(row), (int)boneParent.Integer(row),
                        new Vector3((float)trs[0].Real(row), (float)trs[1].Real(row), (float)trs[2].Real(row)),
                        new Quaternion((float)trs[3].Real(row), (float)trs[4].Real(row), (float)trs[5].Real(row), (float)trs[6].Real(row)),
                        new Vector3((float)trs[7].Real(row), (float)trs[8].Real(row), (float)trs[9].Real(row)), bonePath.Text(row)));
                }
            }
            string key = path + "|lod" + chosenLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);
            List<string> bonePaths = bones.Select(bone => skeleton.FirstOrDefault(entry => entry.Name == bone).Path ?? bone).ToList();
            statement.Add(new StatementMesh
            {
                Key = key, Name = export, Geometry = decoded, BonePaths = bonePaths, Lod = chosenLevel,
            });
            library[path] = new LibraryEntry(decoded, own, bones, skeleton, key);
        }
        return library;
    }

    private static DecodedMesh Decode(ColumnTable geometry, int row, string name)
    {
        DecodedMesh decoded = new(name) { Positions = Floats(geometry["positions"].Bytes(row)) };
        decoded.VertexCount = decoded.Positions.Length / 3;
        decoded.Normals = OptionalFloats(geometry["normals"].Bytes(row));
        decoded.Tangents = OptionalFloats(geometry["tangents"].Bytes(row));
        decoded.Colors = OptionalFloats(geometry["colors"].Bytes(row));
        float[] uv = Floats(geometry["uv"].Bytes(row));
        string joinedSets = geometry["uvSets"].Text(row);
        int[] sets = joinedSets.Length > 0 ? joinedSets.Split(Separator).Select(int.Parse).ToArray() : [];
        if (sets.Length > 0 && uv.Length > 0)
        {
            int stride = uv.Length / sets.Length;
            for (int index = 0; index < sets.Length; index++)
            {
                decoded.Uvs[sets[index]] = uv.AsSpan(index * stride, stride).ToArray();
            }
        }
        decoded.Triangles = MemoryMarshal.Cast<byte, uint>(geometry["indices"].Bytes(row)).ToArray();
        ReadOnlySpan<int> sections = MemoryMarshal.Cast<byte, int>(geometry["sections"].Bytes(row));
        int triangleCount = decoded.Triangles.Length / 3;
        int[] triangleMaterial = new int[triangleCount];
        for (int index = 0; index + 2 < sections.Length; index += 3)
        {
            int first = sections[index] / 3;
            int count = sections[index + 1] / 3;
            for (int triangle = first; triangle < first + count && triangle < triangleCount; triangle++)
            {
                triangleMaterial[triangle] = sections[index + 2];
            }
            decoded.SubMeshes.Add(new DecodedSubMesh(sections[index], sections[index + 1], 0, 0, 0, 0));
        }
        decoded.TriangleMaterial = triangleMaterial;
        ReadOnlySpan<byte> skin = geometry["skin"].Bytes(row);
        if (skin.Length > 0)
        {
            int vertexCount = skin.Length / 32;
            float[] weights = new float[vertexCount * 4];
            int[] indices = new int[vertexCount * 4];
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                for (int slot = 0; slot < 4; slot++)
                {
                    weights[vertex * 4 + slot] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(skin.Slice(vertex * 32 + slot * 4, 4));
                    indices[vertex * 4 + slot] = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(skin.Slice(vertex * 32 + 16 + slot * 4, 4));
                }
            }
            decoded.InfluenceCount = 4;
            decoded.BoneWeights = weights;
            decoded.BoneIndices = indices;
        }
        return decoded;
    }

    private static float[] Floats(ReadOnlySpan<byte> bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();

    private static float[]? OptionalFloats(ReadOnlySpan<byte> bytes) => bytes.Length == 0 ? null : Floats(bytes);

    /// <summary>Every material any placement draws with, resolved the way the engine resolves
    /// it, read once each -- and the textures they name, decoded once each.</summary>
    private static Dictionary<string, string> Materials(string seed, Statement statement, ColumnTable rows,
        Dictionary<string, LibraryEntry> library, CabTable map, TextureRoles roles)
    {
        SortedSet<string> wanted = new(StringComparer.Ordinal);
        Column mesh = rows["mesh"];
        Column materials = rows["materials"];
        for (int row = 0; row < rows.RowCount; row++)
        {
            string stated = materials.Text(row);
            IEnumerable<string> paths = stated.Length > 0
                ? stated.Split(Separator)
                : library.TryGetValue(mesh.Text(row), out LibraryEntry? entry) ? entry.OwnMaterials : [];
            foreach (string path in paths)
            {
                if (path.Length > 0)
                {
                    wanted.Add(path);
                }
            }
        }
        Dictionary<string, string> keys = new(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return keys;
        }
        List<string> arguments = [];
        foreach (string path in wanted)
        {
            arguments.Add(UnrealDatasets.MaterialParam);
            arguments.Add(path);
        }
        (_, ColumnTable table) = Datasets.Table(UnrealDatasets.MaterialsId, arguments.ToArray(), default, map);
        Column owner = table["material"];
        Column kind = table["kind"];
        Column name = table["name"];
        Column texture = table["texture"];
        Column x = table["x"];
        Column y = table["y"];
        Column z = table["z"];
        Column w = table["w"];
        Dictionary<string, (string Name, string Shader, List<KeyValuePair<string, string>> Textures, List<KeyValuePair<string, float>> Floats,
            List<KeyValuePair<string, float[]>> Colors, List<string> Keywords)> gathered = new(StringComparer.Ordinal);
        for (int row = 0; row < table.RowCount; row++)
        {
            string path = owner.Text(row);
            if (!gathered.TryGetValue(path, out var entry))
            {
                entry = (string.Empty, string.Empty, [], [], [], []);
            }
            switch (kind.Text(row))
            {
                case StatementTables.MaterialRow:
                    entry.Name = name.Text(row);
                    entry.Shader = texture.Text(row);
                    break;
                case StatementTables.KeywordRow:
                    entry.Keywords.Add(name.Text(row));
                    break;
                case StatementTables.TextureRow:
                    if (texture.Text(row).Length > 0)
                    {
                        entry.Textures.Add(new KeyValuePair<string, string>(name.Text(row), texture.Text(row)));
                    }
                    break;
                case StatementTables.ScalarRow:
                    entry.Floats.Add(new KeyValuePair<string, float>(name.Text(row), (float)x.Real(row)));
                    break;
                case StatementTables.VectorRow:
                    entry.Colors.Add(new KeyValuePair<string, float[]>(name.Text(row),
                        [(float)x.Real(row), (float)y.Real(row), (float)z.Real(row), (float)w.Real(row)]));
                    break;
            }
            gathered[path] = entry;
        }
        SortedSet<string> textures = new(StringComparer.Ordinal);
        foreach ((string path, var entry) in gathered)
        {
            UnityMaterialProperties properties = new()
            {
                Name = entry.Name.Length > 0 ? entry.Name : path[(path.LastIndexOf('.') + 1)..],
                ShaderName = entry.Shader,
                Textures = entry.Textures,
                TextureAssets = [],
                TextureScaleOffset = new Dictionary<string, float[]>(StringComparer.Ordinal),
                FloatEntries = entry.Floats,
                ColorEntries = entry.Colors,
                KeywordList = entry.Keywords,
                DisabledPasses = [],
            };
            statement.Add(new StatementMaterial { Key = path, Properties = properties, Roles = roles.Resolve(properties) });
            keys[path] = path;
            foreach ((_, string texturePath) in entry.Textures)
            {
                textures.Add(texturePath);
            }
        }
        if (textures.Count > 0)
        {
            List<string> textureArguments = [];
            foreach (string path in textures)
            {
                textureArguments.Add(UnrealDatasets.TextureParam);
                textureArguments.Add(path);
            }
            (_, ColumnTable decoded) = Datasets.Table(UnrealDatasets.TexturesId, textureArguments.ToArray(), default, map);
            Column key = decoded["texture"];
            Column textureName = decoded["name"];
            Column srgb = decoded["srgb"];
            Column image = decoded["image"];
            for (int row = 0; row < decoded.RowCount; row++)
            {
                statement.Add(new StatementTexture
                {
                    Key = key.Text(row), Name = textureName.Text(row), Srgb = srgb.Truthy(row), Container = "png",
                    Image = image.Bytes(row).ToArray(),
                });
            }
        }
        return keys;
    }
}
