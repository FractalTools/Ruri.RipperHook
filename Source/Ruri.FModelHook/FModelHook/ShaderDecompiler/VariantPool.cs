using System.Security.Cryptography;
using System.Text;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// One archive's shader variants, each written once however many of its maps use it.
///
/// A shader map names the shaders the engine compiled it to, and the archive hands the same
/// shader to every map that compiled to it: a depth-only vertex program is one blob that
/// thousands of materials share. Writing each map's variants under the map's own folder wrote
/// that blob out again per map -- measured on one shipped title, two thirds of four hundred
/// thousand written files were a copy of one already there.
///
/// What is NOT shared is the naming. The same shader reads the material constant buffer, and
/// this pipeline names that buffer's members from the material's own parameters, so one shader
/// under two materials is the same code with different member names -- the most useful thing
/// the output says. So the file is keyed by its own CONTENT, not by the shader it came from:
/// identical text is written once, text that differs by a single member name is two files. That
/// is exact rather than nearly exact, and it is what makes the sharing lossless.
///
/// The provenance a variant used to carry in its header -- which map, which material, which slot
/// of that map -- is what made identical code look different, and it is already stated by the
/// .shader that includes the file. So the pooled file states only what is true of the shader
/// itself, and the map states the rest.
/// </summary>
internal sealed class VariantPool
{
    public const string FolderName = "_Shaders";

    private readonly string directory;
    private readonly Dictionary<string, string> pathByProgram = new(StringComparer.Ordinal);
    private bool created;

    public VariantPool(string archiveDirectory)
    {
        directory = Path.Combine(archiveDirectory, FolderName);
    }

    /// <summary>How many files this run put in the pool, and how many writes the pool absorbed.</summary>
    public int Written { get; private set; }

    public int Shared { get; private set; }

    /// <summary>
    /// The pool file holding this text, written if it is not there yet. The name carries the
    /// variant's own keyword so a reader can still tell what it is, and a short digest of the
    /// text so two spellings of one shader never land on the same name.
    /// </summary>
    public string Include(string variantKeyword, string text)
    {
        string digest = Digest(text);
        string fileName = variantKeyword + "_" + digest + ".hlsl";
        if (pathByProgram.TryGetValue(fileName, out string? already))
        {
            return already;
        }

        string path = Path.Combine(directory, fileName);
        if (!created)
        {
            Directory.CreateDirectory(directory);
            created = true;
        }
        if (File.Exists(path))
        {
            Shared++;
        }
        else
        {
            File.WriteAllText(path, text);
            Written++;
        }

        string relative = FolderName + "/" + fileName;
        pathByProgram[fileName] = relative;
        return relative;
    }

    private static string Digest(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 6);
    }
}
