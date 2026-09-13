using System;
using System.Collections.Generic;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// One material's uniform buffer as the engine actually built it: every resource slot in the
/// order the cooked layout lists them, so a shader's resource index IS an index into this.
///
/// The order, the member names and which kind sits in which bucket all come from the engine's
/// own recipe (<see cref="MaterialUniformBufferRecipe"/>), replayed with this material's counts.
/// They used to be written out here by hand, in UE5's order -- which silently renamed every
/// pre-UE5 build's volume textures as cube arrays, its virtual textures as volumes, and put the
/// resource ORDER out of step from the first non-2D texture onward. A join that is exact needs
/// no ordering assumption, and this is the join.
/// </summary>
internal sealed class MaterialUniformBufferLayout
{
    private readonly List<string> _resourceMemberNames;
    private readonly Dictionary<string, string> _typedSlotByAuthorName;
    private readonly Dictionary<string, (int Bucket, int Slot)> _slotByMemberName;

    /// <summary>What one material states about itself: how many of each kind it holds, and what it calls them.</summary>
    public sealed record MaterialResources(
        MaterialUniformBufferRecipe.Counts Counts,
        IReadOnlyList<IReadOnlyList<string?>> TextureAuthorNamesByBucket,
        IReadOnlyList<string?> ExternalAuthorNames);

    public MaterialUniformBufferLayout(MaterialResources material)
    {
        ArgumentNullException.ThrowIfNull(material);
        MaterialUniformBufferRecipe recipe = MaterialUniformBufferRecipe.Current;
        _resourceMemberNames = new List<string>();
        _typedSlotByAuthorName = new Dictionary<string, string>(StringComparer.Ordinal);
        _slotByMemberName = new Dictionary<string, (int, int)>(StringComparer.Ordinal);

        // Every texture member the recipe emits for a bucket is numbered by slot, and the sampler
        // that follows it carries the same number -- so one pass over the replay recovers both
        // the engine's own name and which (bucket, slot) it stands for, without this side
        // knowing one member name.
        Dictionary<string, int> bucketByTemplateStem = new(StringComparer.Ordinal);
        foreach (MaterialUniformBufferRecipe.Member member in recipe.Members)
        {
            if (member.RepeatOver.StartsWith(MaterialUniformBufferRecipe.TexturesPrefix, StringComparison.Ordinal)
                && member.Ubmt is "UBMT_TEXTURE" or "UBMT_SRV")
            {
                int bucket = recipe.BucketOf(member.RepeatOver[MaterialUniformBufferRecipe.TexturesPrefix.Length..]);
                if (bucket >= 0)
                {
                    bucketByTemplateStem[Stem(member.NameTemplate)] = bucket;
                }
            }
        }

        foreach (MaterialUniformBufferRecipe.Placed placed in recipe.Replay(material.Counts))
        {
            if (!placed.IsResource)
            {
                continue;
            }
            (string stem, int slot) = Split(placed.Name);
            string? author = AuthorOf(material, recipe, stem, bucketByTemplateStem, slot);
            bool sampler = placed.Ubmt == "UBMT_SAMPLER";
            string named = author is null ? placed.Name
                : sampler ? author + "Sampler"
                : author;
            _resourceMemberNames.Add(named);

            if (author is not null && named != placed.Name)
            {
                _typedSlotByAuthorName[named] = placed.Name;
            }
            if (!sampler && bucketByTemplateStem.TryGetValue(stem, out int bucketIndex))
            {
                _slotByMemberName[placed.Name] = (bucketIndex, slot);
            }
        }
    }

    public bool TryResolveAuthorName(string authorName, out string typedSlot)
        => _typedSlotByAuthorName.TryGetValue(authorName, out typedSlot!);

    public string? ResolveResourceName(SrtRecord record)
    {
        int index = record.ResourceIndex;
        return index < 0 || index >= _resourceMemberNames.Count
            ? null
            : $"Material_{_resourceMemberNames[index]}";
    }

    public IReadOnlyList<string> ResourceMemberNames => _resourceMemberNames;

    /// <summary>
    /// The texture bucket and slot a member name states, the inverse of the naming above. Read
    /// off the replay this layout was built from, so it answers in the mounted engine's own
    /// bucket numbering and never in some other version's.
    /// </summary>
    public bool TryParseTextureSlot(string memberName, out int bucket, out int slot)
    {
        if (_slotByMemberName.TryGetValue(memberName, out (int Bucket, int Slot) found))
        {
            bucket = found.Bucket;
            slot = found.Slot;
            return true;
        }
        bucket = -1;
        slot = -1;
        return false;
    }

    private static string? AuthorOf(MaterialResources material, MaterialUniformBufferRecipe recipe,
        string stem, IReadOnlyDictionary<string, int> bucketByTemplateStem, int slot)
    {
        IReadOnlyList<string?>? names = null;
        if (bucketByTemplateStem.TryGetValue(stem, out int bucket))
        {
            names = bucket < material.TextureAuthorNamesByBucket.Count
                ? material.TextureAuthorNamesByBucket[bucket]
                : null;
        }
        else if (ExternalStem(recipe, stem))
        {
            names = material.ExternalAuthorNames;
        }
        if (names is null || slot < 0 || slot >= names.Count)
        {
            return null;
        }
        string sanitized = SanitizeHlslIdent(names[slot]);
        return sanitized.Length == 0 ? null : sanitized;
    }

    private static bool ExternalStem(MaterialUniformBufferRecipe recipe, string stem)
    {
        foreach (MaterialUniformBufferRecipe.Member member in recipe.Members)
        {
            if (member.RepeatOver == MaterialUniformBufferRecipe.ExternalTextures
                && member.Ubmt == "UBMT_TEXTURE"
                && Stem(member.NameTemplate) == stem)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A name template without its number and without the suffix that follows it: "Texture2D_%dSampler" states "Texture2D".</summary>
    private static string Stem(string template)
    {
        int at = template.IndexOf("_%d", StringComparison.Ordinal);
        return at < 0 ? template : template[..at];
    }

    /// <summary>A replayed member back into the stem it was numbered from and the number itself.</summary>
    private static (string Stem, int Slot) Split(string name)
    {
        const string samplerSuffix = "Sampler";
        string body = name.EndsWith(samplerSuffix, StringComparison.Ordinal)
            ? name[..^samplerSuffix.Length]
            : name;
        int underscore = body.LastIndexOf('_');
        if (underscore <= 0 || underscore == body.Length - 1
            || !int.TryParse(body[(underscore + 1)..], out int slot))
        {
            return (name, -1);
        }
        return (body[..underscore], slot);
    }

    private static string SanitizeHlslIdent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "None", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[raw.Length];
        int written = 0;
        foreach (char c in raw)
        {
            char ch = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_';
            buffer[written++] = ch;
        }

        if (written == 0)
        {
            return string.Empty;
        }

        if (buffer[0] >= '0' && buffer[0] <= '9')
        {
            return "_" + new string(buffer[..written]);
        }

        return new string(buffer[..written]);
    }
}
