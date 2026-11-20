using CUE4Parse.MappingsProvider.Usmap;
using Ruri.FModelHook.BlenderBridge.TypeTree;

namespace Ruri.Tpk.Unreal;

/// <summary>
/// The Unreal lane of the packer: a .usmap reflection dump becomes a tpk of the Unreal custom
/// engine, every struct a class restated as Unity type trees, the same blobs the runtime builds
/// from the mounted schema. Packed to a file so the trees can be inspected, diffed and shipped;
/// the runtime never needs the file, it registers the same blobs from the schema it mounts.
///
/// Wholly apart from the Unity lane: no dump root, no lineage folders, no stock engine chain.
/// </summary>
internal static class UnrealTpkLane
{
    public const string Switch = "--unreal";

    public static int Run(string[] args)
    {
        if (args.Length is < 1 or > 3)
        {
            throw new ArgumentException($"Usage: Ruri.Tpk {Switch} <mappings.usmap> [<output.tpk>] [<unity layout version>]");
        }
        string usmapPath = Path.GetFullPath(args[0]);
        if (!File.Exists(usmapPath))
        {
            throw new FileNotFoundException("Mappings file not found.", usmapPath);
        }
        string outputPath = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.ChangeExtension(usmapPath, ".tpk");
        string layoutVersion = args.Length > 2 ? args[2] : UsmapTypeTreeBuilder.LayoutVersion.ToString();

        Console.WriteLine($"[Unreal] mappings={usmapPath}");
        Console.WriteLine($"[Unreal] output={outputPath}");

        System.Diagnostics.Stopwatch phase = System.Diagnostics.Stopwatch.StartNew();
        UsmapParser parser = new(usmapPath);
        if (parser.Mappings is null)
        {
            throw new InvalidDataException($"[Unreal] {usmapPath} holds no mappings.");
        }
        Console.WriteLine($"[Unreal] usmap version={parser.Version} structs={parser.Mappings.Types.Count} enums={parser.Mappings.Enums.Count} ({phase.ElapsedMilliseconds} ms)");

        phase.Restart();
        UsmapTypeTreeBuilder builder = UsmapTypeTreeBuilder.Build(parser.Mappings);
        Console.WriteLine($"[Unreal] type trees built ({phase.ElapsedMilliseconds} ms)");
        LineageTpkFile.Write(builder.Blobs, CustomEngineType.UnrealEngine, UsmapTypeTreeBuilder.VersionKey, layoutVersion, outputPath);
        Console.WriteLine($"[Unreal] Wrote {builder.ClassIds.Count} classes in {builder.Blobs.Count} blob(s): {new FileInfo(outputPath).Length / 1024.0 / 1024.0:F2} MB");
        return 0;
    }
}
