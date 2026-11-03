using System.Globalization;
using System.Text;
using AssetRipper.Import.Logging;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Ruri.RipperHook.AR;
using Ruri.RipperHook.BlenderBridge.Tables;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>
/// The install's own native code as text a person reads: the machine code of the methods a question names, each
/// instruction at its address and every call, field, type, method, string and constant under the names the metadata
/// gives it, the cold chunks the compiler moved out of each body, the layout of the type it belongs to -- and the
/// table of every address those names come from. Not part of an import: a question asked on its own about the build,
/// and the only source for what a game's code does when the decompiled copies lying around are a build behind.
/// </summary>
public static class Il2CppDatasets
{
    public const string ListingId = "core.il2cpp.listing";
    public const string SymbolsId = "core.il2cpp.symbols";

    private const string Output = "output";
    private const string AssemblyFilter = "assembly";
    private const string TypeFilter = "type";
    private const string MethodFilter = "method";
    private const string Address = "address";
    private const string Limit = "limit";

    public static void Register()
    {
        Datasets.Publish(ListingId, DataRole.Diagnostic,
            [
                DataParam.Text(Output), DataParam.Text(AssemblyFilter, required: false),
                DataParam.Text(TypeFilter, required: false), DataParam.Text(MethodFilter, required: false),
                DataParam.List(Address), DataParam.Integer(Limit, required: false),
            ],
            "The machine code of the install's methods whose type and name contain the given texts, one file per type "
            + "under output, headed by the type's layout: each method with its signature, then every instruction at its "
            + "address under the names the metadata gives calls, fields, type infos, strings and constants, then the "
            + "cold chunks that belong to it. A type the type text names that has no code of its own (an enum, a plain "
            + "struct) is listed by its layout alone, constants with their values. "
            + "Addresses name the functions to list instead, runtime helpers included. "
            + "One row per function with the file it landed in and whether it listed. Naming no type, method or "
            + "address needs a limit: the whole install is half a million listings.",
            Listing);
        Datasets.Publish(SymbolsId, DataRole.Diagnostic, [DataParam.Text(Output)],
            "Every address of the install's native image that has a name, as one tab-separated file at output sorted "
            + "by address: address, address relative to the image, kind, size, assembly and name. Methods and their "
            + "generic instances, the functions between them, the runtime's exports and key functions, and the code its "
            + "registration tables reach: invokers, attribute generators, virtual call stubs, wrappers and module "
            + "initializers.",
            Symbols);
    }

    private static ColumnTable Listing(DataRequest request)
    {
        string output = request.Text(Output);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        string assemblyName = request.Text(AssemblyFilter);
        string typeName = request.Text(TypeFilter);
        string methodName = request.Text(MethodFilter);
        string[] addresses = request.List(Address);
        int limit = request.Integer(Limit);
        if (typeName.Length == 0 && methodName.Length == 0 && addresses.Length == 0 && limit <= 0)
        {
            throw new ArgumentException(
                $"dataset '{ListingId}' lists nothing it was not asked for: name a type, a method or addresses, or a limit.");
        }

        ApplicationAnalysisContext app = Il2CppInstallContext.For(request.GameRoot);
        Directory.CreateDirectory(output);
        TableBuilder table = new(ListingId, "type", "method", "address", "size#", "file", "status");
        table.Role(ColumnRole.Label, "method").Role(ColumnRole.Group, "type");
        if (addresses.Length > 0)
        {
            ListAddresses(request, app, addresses, output, table);
            return table.Build();
        }

        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        int listed = 0;
        foreach (AssemblyAnalysisContext assembly in app.Assemblies)
        {
            if (assemblyName.Length > 0
                && !string.Equals(assembly.CleanAssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (TypeAnalysisContext type in assembly.Types)
            {
                if (typeName.Length > 0 && !type.FullName.Contains(typeName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                List<MethodAnalysisContext> chosen = [.. type.Methods.Where(method => method.UnderlyingPointer != 0
                    && (methodName.Length == 0 || method.Name.Contains(methodName, StringComparison.OrdinalIgnoreCase)))];
                bool layoutOnly = chosen.Count == 0 && typeName.Length > 0 && methodName.Length == 0;
                if (chosen.Count == 0 && !layoutOnly)
                {
                    continue;
                }
                string file = FileOf(output, assembly.CleanAssemblyName + "." + type.FullName, taken);
                using StreamWriter writer = new(file, false, new UTF8Encoding(false));
                WriteLayout(writer, assembly, type);
                if (layoutOnly)
                {
                    table.Row(type.FullName, string.Empty, string.Empty, 0d, file, "layout");
                    continue;
                }
                foreach (MethodAnalysisContext method in chosen)
                {
                    request.Cancellation.ThrowIfCancellationRequested();
                    WriteMethod(writer, app, method, table, file);
                    listed++;
                    if (limit > 0 && listed >= limit)
                    {
                        return table.Build();
                    }
                }
            }
        }
        return table.Build();
    }

    private static void ListAddresses(DataRequest request, ApplicationAnalysisContext app, string[] addresses,
        string output, TableBuilder table)
    {
        Il2CppFunctionTable functions = Il2CppFunctionTable.For(app);
        string file = Path.Combine(output, "addresses.asm");
        using StreamWriter writer = new(file, false, new UTF8Encoding(false));
        foreach (string text in addresses)
        {
            request.Cancellation.ThrowIfCancellationRequested();
            ulong address = ulong.Parse(text.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            ulong start = address;
            ulong end = 0;
            if (functions.TryFind(address, out int index))
            {
                ulong root = functions.RootOf(index);
                if (functions.TryFind(root, out int primary) && functions.StartOf(primary) == root)
                {
                    start = root;
                    end = functions.EndOf(primary);
                }
            }
            if (app.MethodsByAddress.TryGetValue(start, out List<MethodAnalysisContext> methods) && methods.Count > 0)
            {
                WriteMethod(writer, app, methods[0], table, file);
                continue;
            }
            if (end == 0)
            {
                end = Il2CppX86Listing.FindEnd(app, start);
            }
            string status = "ok";
            try
            {
                writer.Write("== sub_");
                writer.Write(start.ToString("X"));
                writer.Write("  VA=0x");
                writer.Write(start.ToString("X"));
                writer.Write("  size=0x");
                writer.Write((end - start).ToString("X"));
                writer.Write('\n');
                writer.Write(Il2CppX86Listing.RenderRange(app, start, end));
                writer.Write('\n');
            }
            catch (Exception exception)
            {
                status = Il2CppX86Listing.Describe(exception);
                writer.Write("!! ");
                writer.Write(status);
                writer.Write("\n\n");
                Logger.Warning(LogCategory.Export, $"[{ListingId}] no listing at 0x{start:X}: {status}");
            }
            table.Row(string.Empty, "sub_" + start.ToString("X"), "0x" + start.ToString("X"), (double)(end - start), file, status);
        }
    }

    private static void WriteMethod(StreamWriter writer, ApplicationAnalysisContext app, MethodAnalysisContext method,
        TableBuilder table, string file)
    {
        string status = "ok";
        int size = 0;
        try
        {
            method.EnsureRawBytes();
            size = method.RawBytes.Length;
            writer.Write("== ");
            writer.Write(Prototype(method));
            writer.Write("\n   VA=0x");
            writer.Write(method.UnderlyingPointer.ToString("X"));
            writer.Write("  RVA=0x");
            writer.Write(method.Rva.ToString("X"));
            writer.Write("  size=0x");
            writer.Write(size.ToString("X"));
            writer.Write('\n');
            writer.Write(Render(app, method));
            writer.Write('\n');
        }
        catch (Exception exception)
        {
            status = Il2CppX86Listing.Describe(exception);
            writer.Write("!! ");
            writer.Write(status);
            writer.Write("\n\n");
            Logger.Warning(LogCategory.Export,
                $"[{ListingId}] no listing for {Signature(method)} at 0x{method.UnderlyingPointer:X}: {status}");
        }
        table.Row(method.DeclaringType.FullName, Signature(method), "0x" + method.UnderlyingPointer.ToString("X"),
            (double)size, file, status);
    }

    private static void WriteLayout(StreamWriter writer, AssemblyAnalysisContext assembly, TypeAnalysisContext type)
    {
        writer.Write("## ");
        writer.Write(type.FullName);
        if (type.BaseType is not null)
        {
            writer.Write(" : ");
            writer.Write(type.BaseType.FullName);
        }
        writer.Write("  [");
        writer.Write(assembly.CleanAssemblyName);
        writer.Write("]\n");
        foreach (FieldAnalysisContext field in type.Fields)
        {
            bool constant = field.IsStatic && field.ConstantValue is not null;
            writer.Write(constant ? "##   const " : field.IsStatic ? "##   static +0x" : "##   +0x");
            if (!constant)
            {
                writer.Write(field.Offset.ToString("X"));
                writer.Write("  ");
            }
            writer.Write(field.FieldType.FullName);
            writer.Write(' ');
            writer.Write(field.Name);
            if (constant)
            {
                writer.Write(" = ");
                writer.Write(Convert.ToString(field.ConstantValue, CultureInfo.InvariantCulture));
            }
            writer.Write('\n');
        }
        writer.Write('\n');
    }

    private static ColumnTable Symbols(DataRequest request)
    {
        string output = request.Text(Output);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ApplicationAnalysisContext app = Il2CppInstallContext.For(request.GameRoot);
        Il2CppFunctionTable functions = Il2CppFunctionTable.For(app);
        List<(ulong Address, string Kind, ulong Size, string Assembly, string Name)> symbols = [];
        HashSet<ulong> named = [];

        foreach ((ulong address, List<MethodAnalysisContext> methods) in app.MethodsByAddress)
        {
            if (address == 0)
            {
                continue;
            }
            bool generic = methods.All(method => method is ConcreteGenericMethodAnalysisContext);
            string[] names = [.. methods.Select(Signature).Distinct().Take(3)];
            string name = string.Join(" | ", names) + (methods.Count > names.Length ? $" | +{methods.Count - names.Length} more" : string.Empty);
            string assembly = methods[0].DeclaringType?.DeclaringAssembly?.CleanAssemblyName ?? string.Empty;
            symbols.Add((address, generic ? "genericMethod" : "method", SizeAt(functions, address), assembly, name));
            named.Add(address);
        }
        foreach ((ulong address, string kind, string name) in Il2CppAsmAnnotator.RuntimeNames(app))
        {
            if (named.Add(address))
            {
                symbols.Add((address, kind, SizeAt(functions, address), string.Empty, name));
            }
        }
        foreach ((ulong address, string kind, string name) in Il2CppRuntimeSymbols.All(app))
        {
            if (address != 0 && named.Add(address))
            {
                symbols.Add((address, kind, SizeAt(functions, address), string.Empty, name));
            }
        }
        for (int index = 0; index < functions.Count; index++)
        {
            ulong start = functions.StartOf(index);
            if (functions.RootOf(index) == start && named.Add(start))
            {
                symbols.Add((start, "function", functions.EndOf(index) - start, string.Empty,
                    Il2CppHelperNamer.TryGetName(app, start) ?? "sub_" + start.ToString("X")));
            }
        }
        symbols.Sort(static (left, right) => left.Address.CompareTo(right.Address));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        Dictionary<string, int> counts = [];
        using (StreamWriter writer = new(output, false, new UTF8Encoding(false)))
        {
            writer.Write("address\trva\tkind\tsize\tassembly\tname\n");
            foreach ((ulong address, string kind, ulong size, string assembly, string name) in symbols)
            {
                request.Cancellation.ThrowIfCancellationRequested();
                writer.Write(address.ToString("X"));
                writer.Write('\t');
                writer.Write((address - functions.ImageBase).ToString("X"));
                writer.Write('\t');
                writer.Write(kind);
                writer.Write('\t');
                writer.Write(size.ToString("X"));
                writer.Write('\t');
                writer.Write(assembly);
                writer.Write('\t');
                writer.Write(name);
                writer.Write('\n');
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
            }
        }
        TableBuilder table = new(SymbolsId, "kind", "listed#", "file");
        foreach ((string kind, int count) in counts.OrderByDescending(pair => pair.Value))
        {
            table.Row(kind, (double)count, Path.GetFullPath(output));
        }
        return table.Build();
    }

    private static ulong SizeAt(Il2CppFunctionTable functions, ulong address) =>
        functions.TryFind(address, out int index) && functions.StartOf(index) == address ? functions.EndOf(index) - address : 0;

    private static string Render(ApplicationAnalysisContext app, MethodAnalysisContext method) =>
        app.InstructionSet is X86InstructionSet
            ? Il2CppX86Listing.Render(app, method, withAddresses: true, withChunks: true)
            : Il2CppAsmAnnotator.Annotate(app, app.InstructionSet.PrintAssembly(method));

    private static string Signature(MethodAnalysisContext method) =>
        $"{method.DeclaringType?.FullName}::{method.Name}({string.Join(", ", method.Parameters.Select(parameter => parameter.ParameterType.FullName))})";

    /// <summary>The method as its native function is declared: the receiver first for an instance method, the
    /// method's own info last -- where each argument arrives follows from this line.</summary>
    private static string Prototype(MethodAnalysisContext method)
    {
        string modifiers = (method.IsStatic ? "static " : string.Empty)
            + (method.IsAbstract ? "abstract " : method.IsVirtual ? $"virtual[{method.Definition?.slot}] " : string.Empty);
        List<string> parameters = [];
        if (!method.IsStatic)
        {
            parameters.Add(method.DeclaringType.FullName + " this");
        }
        parameters.AddRange(method.Parameters.Select(parameter => parameter.ParameterType.FullName + " " + parameter.ParameterName));
        parameters.Add("MethodInfo method");
        return $"{modifiers}{method.ReturnType.FullName} {method.DeclaringType.FullName}::{method.Name}({string.Join(", ", parameters)})";
    }

    private static string FileOf(string directory, string name, HashSet<string> taken)
    {
        string stem = Readable(name);
        string file = Path.Combine(directory, stem + ".asm");
        for (int copy = 2; !taken.Add(file); copy++)
        {
            file = Path.Combine(directory, stem + "_" + copy + ".asm");
        }
        return file;
    }

    private static string Readable(string name)
    {
        char[] made = name.ToCharArray();
        char[] bad = Path.GetInvalidFileNameChars();
        for (int index = 0; index < made.Length; index++)
        {
            if (Array.IndexOf(bad, made[index]) >= 0)
            {
                made[index] = '_';
            }
        }
        return new string(made).Trim();
    }
}
