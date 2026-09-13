using System.Text.Json;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// How the ENGINE builds a material's uniform buffer, read as data and replayed with one
/// material's own counts.
///
/// <c>FUniformExpressionSet::CreateBufferStruct()</c> is the only statement of what that buffer
/// contains, what each member is called and in which order -- and it changes with the engine:
/// 4.26 packs the numbers as VectorExpressions + ScalarExpressions and knows five kinds of
/// texture parameter, 5.x packs one PreshaderBuffer and knows seven, with the kinds in a
/// different order. Every one of those is an engine fact, so Ruri.UEShaderTpkDumper reads them
/// out of the engine source and this replays them.
///
/// Writing that order out by hand instead is what this replaces, and it was wrong: the hand
/// written buckets were UE5's, so on every pre-UE5 build a volume texture was named as a cube
/// array, a virtual texture as a volume, and the virtual count came back zero -- which put the
/// whole resource ORDER out of step and left the binding names to be guessed by declaration
/// position. Nothing downstream may name a bucket, a member or an offset again.
/// </summary>
internal sealed class MaterialUniformBufferRecipe
{
    public const string FolderName = "_MaterialUniformBuffer";
    public const string FileName = "_Layout.json";

    /// <summary>What a member repeats over, as the recipe states it.</summary>
    public const string Once = "Once";
    public const string TexturesPrefix = "Textures:";
    public const string ExternalTextures = "ExternalTextures";
    public const string TextureCollections = "TextureCollections";
    public const string VirtualTextureStacks = "VirtualTextureStacks";

    private const string LayersAbove = "VirtualTextureStackLayersAbove:";

    /// <summary>One member of the recipe, exactly as the engine's own call states it.</summary>
    public sealed record Member(string Name, string NameTemplate, string ShaderType, string Ubmt,
        int Rows, int Columns, string RepeatOver, string Condition,
        string CountSource, int CountMultiply, int CountDivideRoundUp);

    /// <summary>One member the replay produced: what the engine would have called it, and where it lands.</summary>
    public readonly record struct Placed(string Name, string Ubmt, int ByteOffset, int Elements, bool IsResource);

    /// <summary>What a replay needs to know about ONE material -- every count the recipe can ask for.</summary>
    public sealed record Counts(
        IReadOnlyList<int> TexturesByBucket,
        int ExternalTextures,
        int TextureCollections,
        IReadOnlyList<int> VirtualTextureStackLayers,
        int VectorPreshaders,
        int ScalarPreshaders,
        int PreshaderBufferSize);

    private MaterialUniformBufferRecipe(IReadOnlyList<string> textureKinds, IReadOnlyList<Member> members,
        int structAlignment, int pointerAlignment, string source)
    {
        TextureKinds = textureKinds;
        Members = members;
        StructAlignment = structAlignment;
        PointerAlignment = pointerAlignment;
        Source = source;
    }

    /// <summary>The kinds of texture parameter this engine knows, in the order it numbers its buckets.</summary>
    public IReadOnlyList<string> TextureKinds { get; }

    public IReadOnlyList<Member> Members { get; }

    public int StructAlignment { get; }

    public int PointerAlignment { get; }

    public string Source { get; }

    public bool IsStated => Members.Count > 0;

    /// <summary>The recipe of a build with no dump: no members, so every reader answers "unknown" rather than a guess.</summary>
    public static MaterialUniformBufferRecipe Unstated { get; } = new([], [], 16, 8, string.Empty);

    /// <summary>
    /// The recipe the mounted build was cooked with. One statement per run, set where the rest
    /// of that build's engine facts are loaded -- so no reader carries a version of its own.
    /// </summary>
    public static MaterialUniformBufferRecipe Current { get; set; } = Unstated;

    /// <summary>Which bucket a kind sits in for THIS engine, or -1 where it has no such kind.</summary>
    public int BucketOf(string kind)
    {
        for (int bucket = 0; bucket < TextureKinds.Count; bucket++)
        {
            if (string.Equals(TextureKinds[bucket], kind, StringComparison.Ordinal))
            {
                return bucket;
            }
        }
        return -1;
    }

    /// <summary>What this engine calls the <paramref name="slot"/>-th texture of a bucket when the material exposes no name for it.</summary>
    public string GeneratedTextureName(int bucket, int slot)
    {
        if (bucket >= 0 && bucket < TextureKinds.Count)
        {
            string over = TexturesPrefix + TextureKinds[bucket];
            foreach (Member member in Members)
            {
                if (member.RepeatOver == over && member.Ubmt is "UBMT_TEXTURE" or "UBMT_SRV")
                {
                    return Numbered(member.NameTemplate, slot);
                }
            }
        }
        return "Texture_" + slot;
    }

    /// <summary>
    /// The buffer this recipe builds for a material with these counts: every member the engine
    /// would have emitted, in its order, at its offset. Resources come out in the same order the
    /// cooked layout lists them, so a shader's resource index IS an index into them.
    /// </summary>
    public List<Placed> Replay(Counts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        List<Placed> placed = new(Members.Count);
        int offset = 0;
        int at = 0;
        while (at < Members.Count)
        {
            Member first = Members[at];
            if (first.RepeatOver == Once)
            {
                at++;
                int elements = Elements(first, counts);
                if (elements <= 0)
                {
                    continue;
                }
                placed.Add(new Placed(first.Name, first.Ubmt, offset, elements, IsResource: false));
                offset += elements * first.Rows * first.Columns * sizeof(float);
                continue;
            }

            // Consecutive members repeating over the same thing ARE one loop body: the engine
            // writes a texture and the sampler that goes with it inside one `for`, so the two
            // interleave per element. Emitting each member's whole run in turn instead gives
            // every texture first and every sampler after -- the same slots, the wrong ORDER,
            // and a shader's resource index then lands on somebody else's name.
            int body = at;
            while (body < Members.Count && Members[body].RepeatOver == first.RepeatOver)
            {
                body++;
            }
            int repeats = Repeats(first.RepeatOver, counts);
            for (int index = 0; index < repeats; index++)
            {
                for (int member = at; member < body; member++)
                {
                    if (!Holds(Members[member].Condition, index, counts))
                    {
                        continue;
                    }
                    placed.Add(new Placed(Numbered(Members[member].NameTemplate, index),
                        Members[member].Ubmt, offset, 1, IsResource: true));
                    offset += PointerAlignment;
                }
            }
            at = body;
        }
        return placed;
    }

    private static string Numbered(string template, int index) =>
        template.Length == 0 ? "Texture_" + index : template.Replace("%d", index.ToString(), StringComparison.Ordinal);

    /// <summary>How many elements one numeric member holds, as its own count expression states it.</summary>
    private int Elements(Member member, Counts counts)
    {
        int stated = member.CountSource switch
        {
            VirtualTextureStacks => counts.VirtualTextureStackLayers.Count,
            "VectorPreshaders" => counts.VectorPreshaders,
            "ScalarPreshaders" => counts.ScalarPreshaders,
            "PreshaderBufferSize" => counts.PreshaderBufferSize,
            _ when member.CountSource.StartsWith(TexturesPrefix, StringComparison.Ordinal) =>
                TextureCount(member.CountSource[TexturesPrefix.Length..], counts),
            _ => 0,
        };
        if (stated <= 0)
        {
            return 0;
        }
        int divisor = Math.Max(1, member.CountDivideRoundUp);
        return (stated * Math.Max(1, member.CountMultiply) + divisor - 1) / divisor;
    }

    private int Repeats(string repeatOver, Counts counts) => repeatOver switch
    {
        ExternalTextures => counts.ExternalTextures,
        TextureCollections => counts.TextureCollections,
        VirtualTextureStacks => counts.VirtualTextureStackLayers.Count,
        _ when repeatOver.StartsWith(TexturesPrefix, StringComparison.Ordinal) =>
            TextureCount(repeatOver[TexturesPrefix.Length..], counts),
        _ => 0,
    };

    private int TextureCount(string kind, Counts counts)
    {
        int bucket = BucketOf(kind);
        return bucket >= 0 && bucket < counts.TexturesByBucket.Count ? counts.TexturesByBucket[bucket] : 0;
    }

    /// <summary>Whether one element of a repeating member is emitted at all -- stated as the threshold it tests.</summary>
    private static bool Holds(string condition, int index, Counts counts)
    {
        if (condition.Length == 0)
        {
            return true;
        }
        if (condition.StartsWith(LayersAbove, StringComparison.Ordinal)
            && int.TryParse(condition[LayersAbove.Length..], out int threshold))
        {
            return index < counts.VirtualTextureStackLayers.Count
                && counts.VirtualTextureStackLayers[index] > threshold;
        }
        return true;
    }

    public static MaterialUniformBufferRecipe LoadForGame(string? directory, string? gameVersion, bool tryBaseFallback,
        Action<string>? log = null, Action<string>? logError = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            log?.Invoke($"[MaterialUniformBuffer] No engine metadata at '{directory ?? "<null>"}' — the buffer's members stay unnamed.");
            return Unstated;
        }

        foreach (string root in EngineUbMetadataRegistry.BuildScanRoots(directory, gameVersion, tryBaseFallback))
        {
            string file = Path.Combine(root, FolderName, FileName);
            if (!File.Exists(file))
            {
                continue;
            }
            try
            {
                return Read(file, log);
            }
            catch (Exception exception)
            {
                logError?.Invoke($"[MaterialUniformBuffer] {file}: {exception.GetType().Name}: {exception.Message}");
                return Unstated;
            }
        }

        log?.Invoke($"[MaterialUniformBuffer] No recipe for game={gameVersion ?? "<none>"} — the buffer's members stay unnamed.");
        return Unstated;
    }

    private static MaterialUniformBufferRecipe Read(string file, Action<string>? log)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
        JsonElement root = document.RootElement;

        List<string> kinds = [];
        if (root.TryGetProperty("TextureParameterTypes", out JsonElement stated) && stated.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement kind in stated.EnumerateArray())
            {
                kinds.Add(kind.GetString() ?? string.Empty);
            }
        }

        List<Member> members = [];
        if (root.TryGetProperty("Members", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement member in listed.EnumerateArray())
            {
                members.Add(new Member(
                    Text(member, "Name"), Text(member, "NameTemplate"), Text(member, "ShaderType"), Text(member, "Ubmt"),
                    Number(member, "Rows"), Number(member, "Columns"),
                    Text(member, "RepeatOver"), Text(member, "Condition"),
                    Text(member, "CountSource"), Number(member, "CountMultiply"), Number(member, "CountDivideRoundUp")));
            }
        }

        MaterialUniformBufferRecipe recipe = new(kinds, members,
            Number(root, "StructAlignment"), Number(root, "PointerAlignment"), file);
        log?.Invoke($"[MaterialUniformBuffer] {members.Count} member(s) over {kinds.Count} texture kind(s) from '{file}'.");
        return recipe;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
}
