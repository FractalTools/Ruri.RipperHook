using AsmResolver;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AsmResolver.Symbols.Pdb;
using AsmResolver.Symbols.Pdb.Leaves;
using AsmResolver.Symbols.Pdb.Msf;
using AsmResolver.Symbols.Pdb.Records;
using AssetRipper.DocExtraction.MetaData;

namespace AssetRipper.DocExtraction.ConsoleApp;

public static class NativeEnumExtractor
{
    private static IErrorListener ErrorListener { get; } = new DiagnosticBag();

    /// <summary>
    /// 读取 PDB 并将提取的枚举合并到提供的字典中
    /// </summary>
    public static void MergeFromPdb(string pdbPath, Dictionary<string, EnumDocumentation> targetDictionary)
    {
        Console.WriteLine($"[PDB] Parsing: {Path.GetFileName(pdbPath)}");

        try
        {
            MsfFile file = MsfFile.FromFile(pdbPath);
            PdbImage image = PdbImage.FromFile(file, new PdbReaderParameters(ErrorListener));

            HashSet<EnumTypeRecord> enumRecords = GetEnumRecords(image.Symbols);
            int count = 0;

            foreach (EnumTypeRecord record in enumRecords)
            {
                EnumDocumentation? doc = ParseEnumRecord(record);
                if (doc != null)
                {
                    // 这里使用 FullName 作为 Key 进行去重
                    // 如果字典里已经有了，说明之前的 PDB (可能是优先级更高的) 已经添加过了
                    // 或者我们可以选择合并成员。这里采用“不存在则添加”的策略。
                    if (!targetDictionary.ContainsKey(doc.FullName))
                    {
                        targetDictionary.Add(doc.FullName, doc);
                        count++;
                    }
                    else
                    {
                        // 可选：如果已存在，检查是否需要合并成员？
                        // 通常同一个枚举在不同 PDB 里结构是一样的，或者是子集关系。
                        // 如果 2023 版本的信息更全，且我们在 Program.cs 中优先处理了 2023，则这里保持现状即可。
                    }
                }
            }
            Console.WriteLine($"      Extracted {count} new enums.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      Error parsing PDB: {ex.Message}");
        }
    }

    private static HashSet<EnumTypeRecord> GetEnumRecords(IList<ICodeViewSymbol> symbols)
    {
        HashSet<EnumTypeRecord> records = new();
        foreach (ICodeViewSymbol symbol in symbols)
        {
            if (symbol is ConstantSymbol constantSymbol && constantSymbol.ConstantType is EnumTypeRecord enumRecord)
            {
                records.Add(enumRecord);
            }
        }
        return records;
    }

    private static EnumDocumentation? ParseEnumRecord(EnumTypeRecord record)
    {
        if (record.Fields is null)
        {
            return null;
        }

        string cppName = record.Name;
        if (cppName.Contains('<') || cppName.Contains('>'))
        {
            return null; //No generics or anonymous enums
        }
        string[] nameSegments = cppName.Split("::", StringSplitOptions.RemoveEmptyEntries);

        EnumDocumentation documentation = new();
        documentation.Name = nameSegments[^1];
        documentation.FullName = string.Join('.', nameSegments);

        // Handle underlying type
        try
        {
            documentation.ElementType = ((SimpleTypeRecord)record.BaseType!).Kind switch
            {
                SimpleTypeKind.SignedCharacter => ElementType.I1,
                SimpleTypeKind.UnsignedCharacter => ElementType.U1,
                SimpleTypeKind.SByte => ElementType.I1,
                SimpleTypeKind.Byte => ElementType.U1,
                SimpleTypeKind.Int16Short or SimpleTypeKind.Int16 => ElementType.I2,
                SimpleTypeKind.UInt16Short or SimpleTypeKind.UInt16 => ElementType.U2,
                SimpleTypeKind.Int32Long or SimpleTypeKind.Int32 => ElementType.I4,
                SimpleTypeKind.UInt32Long or SimpleTypeKind.UInt32 => ElementType.U4,
                SimpleTypeKind.Int64Quad or SimpleTypeKind.Int64 => ElementType.I8,
                SimpleTypeKind.UInt64Quad or SimpleTypeKind.UInt64 => ElementType.U8,
                _ => throw new NotSupportedException($"Unknown simple type kind")
            };
        }
        catch
        {
            // 如果无法确定底层类型，默认为 I4，或者跳过
            // Console.WriteLine($"Warning: Could not determine base type for {documentation.FullName}, defaulting to I4");
            documentation.ElementType = ElementType.I4;
        }

        foreach (EnumerateField field in record.Fields!.Entries.Cast<EnumerateField>())
        {
            EnumMemberDocumentation enumMember = new();
            enumMember.Name = field.Name;
            try
            {
                enumMember.Value = field.Value switch
                {
                    byte b => unchecked((sbyte)b),
                    ushort us => unchecked((short)us),
                    uint ui => unchecked((int)ui),
                    ulong ul => unchecked((long)ul),
                    sbyte sb => sb,
                    short s => s,
                    int i => i,
                    long l => l,
                    char c => unchecked((short)c),
                    _ => throw new NotSupportedException()
                };
            }
            catch
            {
                continue;
            }

            // 防止重复成员名
            if (!documentation.Members.ContainsKey(enumMember.Name))
            {
                documentation.Members.Add(enumMember.Name, enumMember);
            }
        }

        return documentation;
    }
}