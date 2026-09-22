using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser.TypeTrees;
using SerializedTypeTree = AssetRipper.IO.Files.SerializedFiles.Parser.TypeTrees.TypeTree;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ruri.RipperHook.Core.CabMapping;

/// <summary>
/// Reads named fields out of a serialized object, using the type tree the file carries with it.
///
/// A scan has the bytes of every object and, beside them, the tree that says what those bytes mean.
/// That tree is the only description of a script-defined asset that ships with the game -- the
/// script itself does not -- so a decoder that wants a field of such an asset reads it here rather
/// than counting offsets, which is the same information written down a second time and wrong as
/// soon as a field is added above it.
///
/// Only the wanted fields are converted; everything else is skipped by size, so asking for two
/// fields of a large asset costs the walk and not the asset.
/// </summary>
public static class SerializedTypeTreeFields
{
    private const TransferMetaFlags AlignBytes = TransferMetaFlags.AlignBytes;

    /// <summary>
    /// The wanted top-level fields of one object, as text. Missing fields are absent from the
    /// result; a tree this reader cannot walk yields what it managed before stopping.
    /// </summary>
    public static Dictionary<string, string> Read(SerializedTypeTree tree, ReadOnlySpan<byte> data,
        bool bigEndian, IReadOnlyCollection<string> wanted)
    {
        Dictionary<string, string> found = new(wanted.Count, StringComparer.Ordinal);
        List<TypeTreeNode> nodes = tree.Nodes;
        if (nodes.Count == 0)
        {
            return found;
        }

        Walker walker = new(nodes, data, bigEndian, wanted, found);
        try
        {
            int index = 1;
            while (index < nodes.Count && nodes[index].Level == 1)
            {
                index = walker.Field(index);
            }
        }
        catch (Exception)
        {
            // A tree that does not describe these bytes stops the walk; what was read before that
            // is still what the file said.
        }
        return found;
    }

    /// <summary>Whether a tree declares a top-level field -- how a decoder recognises its asset.</summary>
    public static bool Declares(SerializedTypeTree tree, string field)
    {
        foreach (TypeTreeNode node in tree.Nodes)
        {
            if (node.Level == 1 && node.Name == field)
            {
                return true;
            }
        }
        return false;
    }

    private ref struct Walker
    {
        private readonly List<TypeTreeNode> _nodes;
        private readonly ReadOnlySpan<byte> _data;
        private readonly bool _bigEndian;
        private readonly IReadOnlyCollection<string> _wanted;
        private readonly Dictionary<string, string> _found;
        private int _at;

        public Walker(List<TypeTreeNode> nodes, ReadOnlySpan<byte> data, bool bigEndian,
            IReadOnlyCollection<string> wanted, Dictionary<string, string> found)
        {
            _nodes = nodes;
            _data = data;
            _bigEndian = bigEndian;
            _wanted = wanted;
            _found = found;
            _at = 0;
        }

        /// <summary>Read the field at <paramref name="index"/>; answer the index after its subtree.</summary>
        public int Field(int index)
        {
            TypeTreeNode node = _nodes[index];
            bool interesting = node.Level == 1 && Contains(node.Name);
            int start = _at;
            int next = Value(index);
            if (interesting)
            {
                _found[node.Name] = Text(node, start);
            }
            return next;
        }

        private bool Contains(string name)
        {
            foreach (string candidate in _wanted)
            {
                if (string.Equals(candidate, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private int Value(int index)
        {
            TypeTreeNode node = _nodes[index];
            int childLevel = node.Level + 1;
            bool hasChildren = index + 1 < _nodes.Count && _nodes[index + 1].Level == childLevel;

            if (!hasChildren)
            {
                _at += node.ByteSize;
                Align(node);
                return index + 1;
            }

            if (node.Type == "string")
            {
                // The characters are the array's elements, so the sequence has already consumed
                // them; adding the length again would advance past every string twice.
                int after = Sequence(index + 1, out _);
                Align(node);
                return after;
            }

            if (_nodes[index + 1].Type == "Array")
            {
                int after = Sequence(index + 1, out _);
                Align(node);
                return after;
            }

            int child = index + 1;
            while (child < _nodes.Count && _nodes[child].Level == childLevel)
            {
                child = Value(child);
            }
            Align(node);
            return child;
        }

        /// <summary>An Array node: a count, then that many elements. Answers the index after it.</summary>
        private int Sequence(int arrayIndex, out int count)
        {
            TypeTreeNode arrayNode = _nodes[arrayIndex];
            int sizeIndex = arrayIndex + 1;
            count = Int32();
            int elementIndex = SkipSubtree(sizeIndex);

            TypeTreeNode element = _nodes[elementIndex];
            bool fixedSize = elementIndex + 1 >= _nodes.Count
                || _nodes[elementIndex + 1].Level <= element.Level;
            if (fixedSize && element.ByteSize > 0 && (element.MetaFlag & AlignBytes) == 0)
            {
                _at += count * element.ByteSize;
            }
            else
            {
                for (int item = 0; item < count; item++)
                {
                    Value(elementIndex);
                }
            }
            Align(arrayNode);
            return SkipSubtree(elementIndex);
        }

        private int SkipSubtree(int index)
        {
            int level = _nodes[index].Level;
            int next = index + 1;
            while (next < _nodes.Count && _nodes[next].Level > level)
            {
                next++;
            }
            return next;
        }

        private void Align(TypeTreeNode node)
        {
            if ((node.MetaFlag & AlignBytes) != 0)
            {
                _at = (_at + 3) & ~3;
            }
        }

        private int Int32()
        {
            ReadOnlySpan<byte> slice = _data.Slice(_at, sizeof(int));
            _at += sizeof(int);
            return _bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(slice)
                : BinaryPrimitives.ReadInt32LittleEndian(slice);
        }

        private string Text(TypeTreeNode node, int start)
        {
            if (node.Type == "string")
            {
                int length = _bigEndian
                    ? BinaryPrimitives.ReadInt32BigEndian(_data.Slice(start, sizeof(int)))
                    : BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(start, sizeof(int)));
                return Encoding.UTF8.GetString(_data.Slice(start + sizeof(int), length));
            }

            ReadOnlySpan<byte> slice = _data[start..Math.Min(_at, _data.Length)];
            return node.Type switch
            {
                "SInt8" => ((sbyte)slice[0]).ToString(CultureInfo.InvariantCulture),
                "UInt8" or "char" => slice[0].ToString(CultureInfo.InvariantCulture),
                "bool" => (slice[0] != 0).ToString(),
                "SInt16" => Number16(slice).ToString(CultureInfo.InvariantCulture),
                "UInt16" or "unsigned short" => ((ushort)Number16(slice)).ToString(CultureInfo.InvariantCulture),
                "SInt32" or "int" => Number32(slice).ToString(CultureInfo.InvariantCulture),
                "UInt32" or "unsigned int" => ((uint)Number32(slice)).ToString(CultureInfo.InvariantCulture),
                "SInt64" or "long long" => Number64(slice).ToString(CultureInfo.InvariantCulture),
                "UInt64" or "unsigned long long" => ((ulong)Number64(slice)).ToString(CultureInfo.InvariantCulture),
                "float" => BitConverter.Int32BitsToSingle(Number32(slice)).ToString("R", CultureInfo.InvariantCulture),
                "double" => BitConverter.Int64BitsToDouble(Number64(slice)).ToString("R", CultureInfo.InvariantCulture),
                _ => Convert.ToHexString(slice),
            };
        }

        private short Number16(ReadOnlySpan<byte> slice) => _bigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(slice)
            : BinaryPrimitives.ReadInt16LittleEndian(slice);

        private int Number32(ReadOnlySpan<byte> slice) => _bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(slice)
            : BinaryPrimitives.ReadInt32LittleEndian(slice);

        private long Number64(ReadOnlySpan<byte> slice) => _bigEndian
            ? BinaryPrimitives.ReadInt64BigEndian(slice)
            : BinaryPrimitives.ReadInt64LittleEndian(slice);
    }
}
