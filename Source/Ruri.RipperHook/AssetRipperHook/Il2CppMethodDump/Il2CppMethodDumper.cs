using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AssetRipper.Export.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IlApi = Cpp2IL.Core.Cpp2IlApi;

namespace Ruri.RipperHook.AR;

/// <summary>
/// 遍历 Cpp2IL 已经建立好的 IL2CPP 分析上下文（<see cref="Cpp2IlApi.CurrentAppContext"/>），
/// 对每个方法解析其在 GameAssembly 中的函数指针（VA / RVA），并反汇编原生方法体。
/// 按程序集写出 <c>{Assembly}.asm</c>，并额外写一份全局 <c>MethodPointers.csv</c> 指针索引。
/// </summary>
internal static class Il2CppMethodDumper
{
    private const string OutputFolderName = "Il2CppMethodDump";

    public static void Dump(FullConfiguration settings, FileSystem fileSystem)
    {
        ApplicationAnalysisContext app = Cpp2IlApi.CurrentAppContext;
        if (app == null)
        {
            Logger.Warning(LogCategory.Export, "[Il2CppMethodDump] Cpp2IL app context is null; nothing to disassemble.");
            return;
        }

        string outputDirectory = fileSystem.Path.Join(settings.AuxiliaryFilesPath, OutputFolderName);
        fileSystem.Directory.Create(outputDirectory);

        string instructionSet = app.InstructionSet.GetType().Name;
        Logger.Info(LogCategory.Export,
            $"[Il2CppMethodDump] Disassembling native method bodies ({instructionSet}, Unity {app.UnityVersion}) → {outputDirectory}");

        Stopwatch stopwatch = Stopwatch.StartNew();
        int totalMethods = 0, dumpedMethods = 0, failedMethods = 0;

        // 全局函数指针索引，便于导入 IDA / Ghidra 等工具。
        string indexPath = fileSystem.Path.Join(outputDirectory, "MethodPointers.csv");
        using (Stream indexStream = fileSystem.File.Create(indexPath))
        using (var indexWriter = new StreamWriter(indexStream, Utf8NoBom))
        {
            indexWriter.WriteLine("Assembly,VA,RVA,Method");

            foreach (AssemblyAnalysisContext assemblyContext in app.Assemblies)
            {
                string assemblyName = ResolveAssemblyName(assemblyContext);
                string asmFilePath = fileSystem.Path.Join(outputDirectory, SanitizeFileName(assemblyName) + ".asm");

                using Stream asmStream = fileSystem.File.Create(asmFilePath);
                using var asmWriter = new StreamWriter(asmStream, Utf8NoBom);

                asmWriter.WriteLine("; ====================================================================");
                asmWriter.WriteLine("; IL2CPP native method disassembly");
                asmWriter.WriteLine($"; Assembly       : {assemblyName}");
                asmWriter.WriteLine($"; Instruction set: {instructionSet}");
                asmWriter.WriteLine($"; Unity version  : {app.UnityVersion}");
                asmWriter.WriteLine("; ====================================================================");

                foreach (TypeAnalysisContext typeContext in assemblyContext.Types)
                {
                    if (typeContext?.Methods == null)
                    {
                        continue;
                    }

                    foreach (MethodAnalysisContext methodContext in typeContext.Methods)
                    {
                        totalMethods++;

                        ulong va = methodContext.UnderlyingPointer;
                        if (va == 0)
                        {
                            continue; // 抽象 / extern / 无原生方法体
                        }

                        string signature = ResolveMethodSignature(methodContext);
                        ulong rva = SafeRva(methodContext);

                        asmWriter.WriteLine();
                        asmWriter.WriteLine("; --------------------------------------------------------------------");
                        asmWriter.WriteLine($"; {signature}");
                        asmWriter.WriteLine($"; VA=0x{va:X}  RVA=0x{rva:X}");
                        asmWriter.WriteLine("; --------------------------------------------------------------------");

                        try
                        {
                            string disassembly = app.InstructionSet.PrintAssembly(methodContext);
                            asmWriter.WriteLine(disassembly);
                            dumpedMethods++;
                        }
                        catch (Exception ex)
                        {
                            asmWriter.WriteLine($"; <disassembly failed: {ex.GetType().Name}: {ex.Message}>");
                            failedMethods++;
                        }

                        indexWriter.WriteLine($"{EscapeCsv(assemblyName)},0x{va:X},0x{rva:X},{EscapeCsv(signature)}");
                    }
                }
            }
        }

        stopwatch.Stop();
        Logger.Info(LogCategory.Export,
            $"[Il2CppMethodDump] Done: {dumpedMethods} disassembled, {failedMethods} failed, {totalMethods} methods scanned in {stopwatch.Elapsed.TotalSeconds:F1}s");
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static ulong SafeRva(MethodAnalysisContext methodContext)
    {
        try { return methodContext.Rva; }
        catch { return 0; }
    }

    private static string ResolveAssemblyName(AssemblyAnalysisContext assemblyContext)
    {
        try
        {
            string name = assemblyContext.CleanAssemblyName;
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }
        catch
        {
            // fall through
        }
        return "Unknown";
    }

    private static string ResolveMethodSignature(MethodAnalysisContext methodContext)
    {
        try { return methodContext.FullNameWithSignature; }
        catch
        {
            try { return methodContext.DefaultName; }
            catch { return "<unknown method>"; }
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }
        return name;
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        if (value.IndexOfAny(['"', ',', '\n', '\r']) < 0)
        {
            return value;
        }
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
