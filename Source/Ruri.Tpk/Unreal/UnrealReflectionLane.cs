using System.Diagnostics;
using CUE4Parse.MappingsProvider.Usmap;
using Ruri.Tpk.Unreal.Reflection;

namespace Ruri.Tpk.Unreal;

/// <summary>
/// The reflection lane of the packer: a game executable that ships its program database
/// becomes the .usmap of its build, read off the static reflection data the compiler laid out
/// -- no game launched, no code injected. The file is what the Unreal decoder mounts as the
/// property schema and what the tpk lane restates as type trees.
///
/// Wholly apart from the Unity lane, like the tpk lane beside it.
/// </summary>
internal static class UnrealReflectionLane
{
    public const string Switch = "--unreal-reflection";

    public static int Run(string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            throw new ArgumentException($"Usage: Ruri.Tpk {Switch} <game executable> [<output.usmap>]");
        }
        string executable = Path.GetFullPath(args[0]);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Game executable not found.", executable);
        }
        string database = Path.ChangeExtension(executable, ".pdb");
        if (!File.Exists(database))
        {
            throw new FileNotFoundException("The executable's program database must sit beside it; the reflection data is located through its symbols.", database);
        }
        string outputPath = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.ChangeExtension(executable, ".usmap");

        Console.WriteLine($"[Unreal] executable={executable}");
        Console.WriteLine($"[Unreal] database={database}");
        Console.WriteLine($"[Unreal] output={outputPath}");

        Stopwatch phase = Stopwatch.StartNew();
        ProgramImage image = new(executable);
        ProgramSymbols symbols = new(database, image);
        Console.WriteLine($"[Unreal] symbols={symbols.Count} imageBase=0x{image.ImageBase:X} ({phase.ElapsedMilliseconds} ms)");

        phase.Restart();
        ReflectedSchema schema = new CodeGenReader(image, symbols).Read();
        int properties = 0;
        foreach (ReflectedStruct structure in schema.Structs)
        {
            properties += structure.Properties.Count;
        }
        Console.WriteLine($"[Unreal] structs={schema.Structs.Count} properties={properties} enums={schema.Enums.Count} ({phase.ElapsedMilliseconds} ms)");
        if (schema.OmittedClasses.Count > 0)
        {
            Console.WriteLine($"[Unreal] {schema.OmittedClasses.Count} intrinsic classes have no type record in the database, so their parent cannot be stated; omitted: {string.Join(", ", schema.OmittedClasses)}");
        }

        phase.Restart();
        UsmapWriter.Write(schema, outputPath);
        Console.WriteLine($"[Unreal] Wrote {new FileInfo(outputPath).Length / 1024.0:F1} KB ({phase.ElapsedMilliseconds} ms)");

        UsmapParser parsed = new(outputPath);
        if (parsed.Mappings is null || parsed.Mappings.Types.Count != schema.Structs.Count || parsed.Mappings.Enums.Count != schema.Enums.Count)
        {
            throw new InvalidDataException(
                $"[Unreal] The written file reads back as structs={parsed.Mappings?.Types.Count ?? 0} enums={parsed.Mappings?.Enums.Count ?? 0}, "
                + $"not the structs={schema.Structs.Count} enums={schema.Enums.Count} that were written.");
        }
        Console.WriteLine($"[Unreal] Read back through CUE4Parse: version={parsed.Version} structs={parsed.Mappings.Types.Count} enums={parsed.Mappings.Enums.Count}");
        return 0;
    }
}
