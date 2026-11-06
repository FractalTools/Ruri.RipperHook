extern alias icedreal;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using icedreal::Iced.Intel;

namespace Ruri.RipperHook.AR;

internal sealed class Il2CppNativeBody
{
    internal sealed record Region(ulong Start, ulong End, long RawOffset, byte[] Bytes, string Source, bool Executable);
    internal readonly record struct SourceRange(ulong Start, ulong End, long RawOffset, string Source, bool Executable);
    internal enum ContentKind { Code, Data, Unknown }
    internal readonly record struct Partition(ulong Start, ulong End, long RawOffset, ContentKind Kind, string Reason);
    internal sealed record DataRange(ulong Start, ulong End, long RawOffset, byte[] Bytes, int EntrySize, ulong[] Targets,
        ulong SourceSite, bool Signed, int ResultBits, ulong Adjustment)
    {
        public string Interpretation => (Signed ? "signed " : "unsigned ") + EntrySize * 8 + "-bit load, "
            + ResultBits + "-bit result, add 0x" + Adjustment.ToString("X");
    }
    internal readonly record struct Issue(ulong Address, string Reason);
    internal readonly record struct UnknownRange(ulong Start, ulong End, long RawOffset, string Reason);
    private sealed record TableAccess(ulong Start, int Count, int Size, bool Signed, int ResultBits);
    private readonly record struct Value(bool HasConstant, ulong Constant, int ExtendedBits, bool Signed,
        bool HasBound = false, ulong Upper = 0, TableAccess Table = null, ulong Adjustment = 0);

    private readonly Il2CppBinary _binary;
    private readonly Il2CppFunctionTable _functions;
    private readonly List<Region> _regions = new();
    private readonly List<DataRange> _dataRanges = new();
    private readonly List<Issue> _issues = new();
    private readonly Dictionary<ulong, List<ulong>> _successors = new();
    private readonly SortedList<ulong, Instruction> _decoded = new();
    private readonly HashSet<ulong> _entries = new();
    private readonly Queue<ulong> _pending = new();
    private readonly HashSet<ulong> _indirect = new();
    private readonly HashSet<ulong> _resolved = new();
    private readonly Dictionary<ulong, int> _incomingCounts = new();
    private bool _boundaryKnown;

    private Il2CppNativeBody(ApplicationAnalysisContext app, ulong entry)
    {
        _binary = app.Binary;
        _functions = Il2CppFunctionTable.For(app);
        Entry = entry;
    }

    public ulong Entry { get; }
    public ulong PrimaryEnd { get; private set; }
    public ulong PrimarySize => PrimaryEnd - Entry;
    public long ByteCount { get; private set; }
    public string Status { get; private set; }
    public IReadOnlyList<Region> Regions => _regions;
    public IReadOnlyList<SourceRange> SourceRanges { get; private set; }
    public IReadOnlyList<Partition> Partitions { get; private set; }
    public IReadOnlyList<DataRange> DataRanges => _dataRanges;
    public IReadOnlyList<Issue> Issues => _issues;
    public IReadOnlyList<UnknownRange> UnknownRanges { get; private set; }
    public IReadOnlyDictionary<ulong, List<ulong>> Successors => _successors;
    public List<Instruction> Instructions { get; private set; }

    public bool IsLinearStep(in Instruction previous, in Instruction current, bool acrossCalls = false) => previous.NextIP == current.IP
        && (acrossCalls || previous.FlowControl is not (FlowControl.Call or FlowControl.IndirectCall))
        && _successors.TryGetValue(previous.IP, out List<ulong> targets) && targets.Contains(current.IP)
        && _incomingCounts.TryGetValue(current.IP, out int count) && count == 1;

    public static Il2CppNativeBody Read(ApplicationAnalysisContext app, ulong entry, ulong requestedEnd = 0,
        bool withChunks = true, int limit = 0x4000)
    {
        Il2CppNativeBody body = new(app, entry);
        ulong end = requestedEnd;
        string source = "requested range";
        body._boundaryKnown = requestedEnd > entry;
        if (end == 0 && body._functions.TryFind(entry, out int own))
        {
            end = body._functions.EndOf(own);
            source = "PE unwind portion";
            body._boundaryKnown = true;
        }
        if (end == 0 && body._functions.TryGetRawRange(body._binary, entry, out _, out ulong mappedEnd, out _))
        {
            end = Math.Min(mappedEnd, entry + (ulong)limit);
            ulong next = MiscUtils.GetAddressOfNextFunctionStart(entry, body._binary);
            if (next > entry)
                end = Math.Min(end, next);
            next = body._functions.NextStart(entry);
            if (next > entry)
                end = Math.Min(end, next);
            source = "bounded search; function end not established";
            body.AddIssue(entry, source);
        }
        body.PrimaryEnd = Math.Max(entry, end);
        body.AddRegion(entry, body.PrimaryEnd, source, seed: true);
        if (withChunks && body._functions.TryFind(entry, out int primary) && body._functions.RootOf(primary) == entry)
        {
            foreach ((ulong start, ulong chunkEnd) in body._functions.ChunksOf(entry))
                body.AddRegion(start, chunkEnd, "PE chained unwind portion", seed: true);
        }
        body.DecodePending();
        bool added;
        do
        {
            added = false;
            foreach (ulong address in body._indirect.ToArray())
            {
                if (!body._resolved.Contains(address) && body.TryResolveTable(address))
                {
                    body._resolved.Add(address);
                    added = true;
                }
            }
            body.DecodePending();
        } while (added);
        body.RevalidateTables();
        foreach (ulong address in body._indirect)
            if (!body._resolved.Contains(address))
                body.AddIssue(address, "indirect branch targets not established");
        body.Finish();
        return body;
    }

    private void AddIssue(ulong address, string reason)
    {
        Issue issue = new(address, reason);
        if (!_issues.Contains(issue))
            _issues.Add(issue);
    }

    private void AddRegion(ulong start, ulong end, string source, bool seed)
    {
        if (end <= start)
            return;
        if (_regions.Any(region => start >= region.Start && end <= region.End))
            return;
        bool mapped = _functions.TryGetRawRange(_binary, start, out long raw, out ulong mappedEnd, out bool executable);
        int length = mapped ? checked((int)(Math.Min(end, mappedEnd) - start)) : 0;
        byte[] bytes = length > 0 ? _binary.ReadByteArrayAtRawAddress(raw, length) : Array.Empty<byte>();
        _regions.Add(new Region(start, end, mapped ? raw : -1, bytes, source, seed && executable));
        if (bytes.Length != (long)(end - start))
            AddIssue(start + (ulong)bytes.Length, "declared range has no file-backed bytes");
        if (seed && executable && bytes.Length > 0)
        {
            _entries.Add(start);
            _pending.Enqueue(start);
        }
        else if (seed)
            AddIssue(start, "code entry is not in a file-backed executable range");
    }

    private Region CodeRegion(ulong address) => _regions.FirstOrDefault(region => region.Executable
        && address >= region.Start && address < region.Start + (ulong)region.Bytes.Length);

    private bool IsData(ulong address) => _dataRanges.Any(data => address >= data.Start && address < data.End);

    private bool OverlapsCode(ulong start, ulong end)
    {
        int index = LowerBound(start);
        if (index > 0 && _decoded.Values[index - 1].NextIP > start)
            return true;
        return index < _decoded.Count && _decoded.Keys[index] < end;
    }

    private int LowerBound(ulong address)
    {
        int low = 0, high = _decoded.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_decoded.Keys[middle] < address)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private void AddEdge(ulong source, ulong target, bool enqueue = true)
    {
        if (!_successors.TryGetValue(source, out List<ulong> targets))
            _successors[source] = targets = new List<ulong>();
        if (!targets.Contains(target))
            targets.Add(target);
        if (CodeRegion(target) == null)
            AddIssue(source, "flow target 0x" + target.ToString("X") + " outside selected file-backed code ranges");
        else if (enqueue)
            _pending.Enqueue(target);
    }

    private void DecodePending()
    {
        while (_pending.Count > 0)
        {
            ulong address = _pending.Dequeue();
            Region active = null;
            Decoder decoder = null;
            while (true)
            {
                if (_decoded.ContainsKey(address))
                    break;
                if (IsData(address))
                {
                    AddIssue(address, "control flow enters a proven data range");
                    break;
                }
                Region region = CodeRegion(address);
                if (region == null)
                    break;
                if (OverlapsCode(address, address + 1))
                {
                    AddIssue(address, "branch target overlaps an existing instruction");
                    break;
                }
                if (!ReferenceEquals(active, region) || decoder.IP != address)
                {
                    int offset = (int)(address - region.Start);
                    ByteArrayCodeReader reader = new(region.Bytes, offset, region.Bytes.Length - offset);
                    decoder = Decoder.Create(_binary.is32Bit ? 32 : 64, reader, address);
                    active = region;
                }
                decoder.Decode(out Instruction instruction);
                if (instruction.IsInvalid)
                {
                    AddIssue(address, "decoder " + decoder.LastError);
                    break;
                }
                if (OverlapsCode(address, instruction.NextIP)
                    || _dataRanges.Any(data => address < data.End && instruction.NextIP > data.Start))
                {
                    AddIssue(address, "instruction overlaps an existing code/data boundary");
                    break;
                }
                _decoded.Add(address, instruction);
                switch (instruction.FlowControl)
                {
                    case FlowControl.ConditionalBranch:
                        if (IsNearBranch(instruction))
                            AddEdge(address, instruction.NearBranchTarget);
                        else
                            AddIssue(address, "conditional branch target form not resolved");
                        AddEdge(address, instruction.NextIP, enqueue: false);
                        break;
                    case FlowControl.UnconditionalBranch:
                        if (IsNearBranch(instruction))
                            AddEdge(address, instruction.NearBranchTarget);
                        else
                            AddIssue(address, "far branch target form not resolved");
                        goto EndBlock;
                    case FlowControl.IndirectBranch:
                        _indirect.Add(address);
                        goto EndBlock;
                    case FlowControl.Return:
                    case FlowControl.Interrupt:
                    case FlowControl.Exception:
                        goto EndBlock;
                    case FlowControl.XbeginXabortXend:
                        AddIssue(address, "transactional control flow not resolved");
                        goto EndBlock;
                    default:
                        AddEdge(address, instruction.NextIP, enqueue: false);
                        break;
                }
                address = instruction.NextIP;
            }
        EndBlock:;
        }
    }

    private Value ReadTableTarget(ulong address)
    {
        Dictionary<ulong, List<ulong>> predecessors = new();
        foreach ((ulong source, List<ulong> destinations) in _successors)
        {
            foreach (ulong destination in destinations)
            {
                if (!predecessors.TryGetValue(destination, out List<ulong> sources))
                    predecessors[destination] = sources = new List<ulong>();
                sources.Add(source);
            }
        }
        List<Instruction> prefix = new();
        HashSet<ulong> seen = new();
        ulong current = address;
        while (seen.Add(current) && _decoded.TryGetValue(current, out Instruction instruction))
        {
            prefix.Add(instruction);
            if (_entries.Contains(current) || instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall
                || !predecessors.TryGetValue(current, out List<ulong> incoming) || incoming.Count != 1
                || !_decoded.TryGetValue(incoming[0], out Instruction previous) || previous.NextIP != current)
                break;
            current = previous.IP;
        }
        prefix.Reverse();
        Value[] values = new Value[16];
        Instruction comparison = default;
        InstructionInfoFactory factory = new();
        foreach (Instruction instruction in prefix)
        {
            if (instruction.IP == address)
                break;
            if (instruction.Mnemonic == Mnemonic.Cmp && instruction.Op0Kind == OpKind.Register && IsImmediate(instruction.Op1Kind))
            {
                comparison = instruction;
                continue;
            }
            if (instruction.FlowControl == FlowControl.ConditionalBranch && comparison.NextIP == instruction.IP
                && instruction.Mnemonic is Mnemonic.Ja or Mnemonic.Jae && instruction.NearBranchTarget != instruction.NextIP)
            {
                int register = RegisterFlowUtil.GpIndex(comparison.Op0Register);
                ulong upper = comparison.GetImmediate(1);
                if (instruction.Mnemonic == Mnemonic.Jae)
                {
                    if (upper == 0)
                        return default;
                    upper--;
                }
                int bits = comparison.Op0Register.GetSize() * 8;
                if (register >= 0 && (bits == (_binary.is32Bit ? 32 : 64)
                    || values[register].ExtendedBits != 0 && values[register].ExtendedBits <= bits
                    && (!values[register].Signed || upper < (1UL << (bits - 1)))))
                    values[register] = values[register] with { HasBound = true, Upper = upper };
                comparison = default;
                continue;
            }
            comparison = default;
            int destination = instruction.Op0Kind == OpKind.Register ? RegisterFlowUtil.GpIndex(instruction.Op0Register) : -1;
            Value result = Evaluate(instruction, values);
            foreach (UsedRegister used in factory.GetInfo(instruction).GetUsedRegisters())
            {
                if (used.Access is not (OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite))
                    continue;
                int index = RegisterFlowUtil.GpIndex(used.Register);
                if (index >= 0)
                    values[index] = new Value(false, 0, _binary.is32Bit ? 32 : used.Register.GetSize() == 4 ? 32 : 64, false);
            }
            if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
                Array.Clear(values);
            else if (destination >= 0)
                values[destination] = result;
        }
        Instruction jump = _decoded[address];
        return jump.Op0Kind == OpKind.Register
            ? values[RegisterFlowUtil.GpIndex(jump.Op0Register)]
            : jump.Op0Kind == OpKind.Memory ? ReadOperand(jump, values, signed: false) : default;
    }

    private bool TryResolveTable(ulong address)
    {
        Value target = ReadTableTarget(address);
        if (target.Table == null)
            return false;
        TableAccess access = target.Table;
        ulong end = access.Start + (ulong)access.Count * (ulong)access.Size;
        if (!_functions.TryGetRawRange(_binary, access.Start, out long raw, out ulong mappedEnd, out _)
            || end > mappedEnd || OverlapsCode(access.Start, end))
            return false;
        if (!_functions.IsReadOnly(access.Start, end))
        {
            AddRegion(access.Start, end, "referenced table bytes; runtime contents not established", seed: false);
            AddIssue(address, "table at 0x" + access.Start.ToString("X") + " is writable or lacks read-only image evidence; runtime targets unknown");
            AddIssue(access.Start, "referenced table has runtime-dependent contents; targets unknown");
            return false;
        }
        byte[] bytes = _binary.ReadByteArrayAtRawAddress(raw, checked(access.Count * access.Size));
        ulong[] targets = new ulong[access.Count];
        for (int index = 0; index < targets.Length; index++)
        {
            ReadOnlySpan<byte> element = bytes.AsSpan(index * access.Size, access.Size);
            ulong value = access.Size switch
            {
                1 => element[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(element),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(element),
                8 => BinaryPrimitives.ReadUInt64LittleEndian(element),
                _ => throw new InvalidOperationException("Unsupported x86 table element size")
            };
            if (access.Signed && access.Size < 8)
                value = (ulong)((long)(value << (64 - access.Size * 8)) >> (64 - access.Size * 8));
            if (access.ResultBits == 32)
                value = (uint)value;
            targets[index] = unchecked(value + target.Adjustment);
            if (_binary.is32Bit)
                targets[index] = (uint)targets[index];
        }
        _dataRanges.Add(new DataRange(access.Start, end, raw, bytes, access.Size, targets,
            address, access.Signed, access.ResultBits, target.Adjustment));
        AddRegion(access.Start, end, "referenced table bytes", seed: false);
        foreach (ulong destination in targets)
            AddEdge(address, destination);
        return true;
    }

    private void RevalidateTables()
    {
        if (_dataRanges.Count == 0)
            return;
        HashSet<ulong> invalid = new();
        foreach (DataRange data in _dataRanges)
        {
            Value target = ReadTableTarget(data.SourceSite);
            TableAccess access = target.Table;
            if (access != null && access.Start == data.Start && access.Count == data.Targets.Length
                && access.Size == data.EntrySize && access.Signed == data.Signed
                && access.ResultBits == data.ResultBits && target.Adjustment == data.Adjustment)
                continue;
            invalid.Add(data.SourceSite);
        }
        if (invalid.Count == 0)
            return;
        foreach (ulong address in invalid)
        {
            _resolved.Remove(address);
            _successors.Remove(address);
            AddIssue(address, "jump table bounds invalidated by additional control-flow predecessors");
        }

        HashSet<ulong> reachable = new();
        Queue<ulong> pending = new(_entries);
        while (pending.TryDequeue(out ulong address))
        {
            if (!_decoded.ContainsKey(address) || !reachable.Add(address))
                continue;
            if (_successors.TryGetValue(address, out List<ulong> targets))
            {
                foreach (ulong target in targets)
                    pending.Enqueue(target);
            }
        }
        foreach (ulong address in _decoded.Keys.Where(address => !reachable.Contains(address)).ToArray())
        {
            _decoded.Remove(address);
            _successors.Remove(address);
            _indirect.Remove(address);
            _resolved.Remove(address);
        }
        _dataRanges.RemoveAll(data => !_resolved.Contains(data.SourceSite));
    }

    private Value Evaluate(in Instruction instruction, Value[] values)
    {
        int destination = instruction.Op0Kind == OpKind.Register ? RegisterFlowUtil.GpIndex(instruction.Op0Register) : -1;
        if (destination < 0)
            return default;
        int bits = instruction.Op0Register.GetSize() * 8;
        Value unknown = new(false, 0, bits == 32 || _binary.is32Bit ? 32 : 64, false);
        if (bits < 32)
            return unknown;
        if (instruction.Mnemonic == Mnemonic.Lea && instruction.Op1Kind == OpKind.Memory && instruction.MemoryIndex == Register.None)
        {
            if (instruction.IsIPRelativeMemoryOperand || instruction.MemoryBase == Register.None)
                return Constant(instruction.MemoryDisplacement64, bits);
            int source = RegisterFlowUtil.GpIndex(instruction.MemoryBase);
            if (source >= 0 && values[source].HasConstant)
                return Constant(unchecked(values[source].Constant + instruction.MemoryDisplacement64), bits);
        }
        if (instruction.Mnemonic is Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx or Mnemonic.Movsxd)
        {
            if (IsImmediate(instruction.Op1Kind))
                return Constant(instruction.GetImmediate(1), bits);
            if (instruction.Op1Kind == OpKind.Register)
            {
                int source = RegisterFlowUtil.GpIndex(instruction.Op1Register);
                if (source >= 0)
                {
                    Value value = values[source];
                    int sourceBits = instruction.Op1Register.GetSize() * 8;
                    bool extend = instruction.Mnemonic is Mnemonic.Movsx or Mnemonic.Movsxd or Mnemonic.Movzx;
                    bool signed = instruction.Mnemonic is Mnemonic.Movsx or Mnemonic.Movsxd;
                    if (value.Table != null && value.Adjustment != 0 && (extend || bits != value.Table.ResultBits))
                        return unknown;
                    if (value.HasConstant)
                    {
                        ulong constant = sourceBits == 64 ? value.Constant : value.Constant & ((1UL << sourceBits) - 1);
                        if (signed && sourceBits < 64)
                            constant = (ulong)((long)(constant << (64 - sourceBits)) >> (64 - sourceBits));
                        return Constant(constant, bits);
                    }
                    if (bits >= 32 && (!extend || value.Table == null
                        || value.Adjustment == 0 && value.Table.Size * 8 == sourceBits))
                    {
                        TableAccess table = value.Table;
                        if (table != null)
                            table = table with { Signed = extend ? signed : table.Signed, ResultBits = bits };
                        return value with { ExtendedBits = bits == 32 ? 32 : extend ? sourceBits
                                : value.ExtendedBits == 0 ? sourceBits : value.ExtendedBits,
                            Signed = bits == 64 && (extend ? signed : value.Signed), Table = table,
                            HasBound = value.HasBound && (!extend || !signed) && (bits == 64 || value.Upper <= uint.MaxValue) };
                    }
                }
            }
            if (instruction.Op1Kind == OpKind.Memory)
            {
                Value value = ReadOperand(instruction, values, instruction.Mnemonic is Mnemonic.Movsx or Mnemonic.Movsxd);
                return value.ExtendedBits != 0 ? value : unknown;
            }
        }
        if (instruction.Mnemonic is Mnemonic.Add or Mnemonic.Sub && bits >= 32)
        {
            Value left = values[destination];
            Value right = instruction.Op1Kind == OpKind.Register
                ? values[RegisterFlowUtil.GpIndex(instruction.Op1Register)]
                : IsImmediate(instruction.Op1Kind) ? Constant(instruction.GetImmediate(1), bits) : default;
            if (right.HasConstant)
            {
                ulong adjustment = instruction.Mnemonic == Mnemonic.Add ? right.Constant : unchecked(0UL - right.Constant);
                if (left.HasConstant)
                    return Constant(unchecked(left.Constant + adjustment), bits);
                if (left.Table != null && bits == 64)
                    return left with { Adjustment = unchecked(left.Adjustment + adjustment) };
            }
        }
        return unknown;
    }

    private Value ReadOperand(in Instruction instruction, Value[] values, bool signed)
    {
        int size = instruction.MemorySize.GetInfo().Size;
        Value unknown = new(false, 0, size * 8, signed);
        if (size is not (1 or 2 or 4 or 8) || instruction.MemoryIndex == Register.None || instruction.MemoryIndexScale != size)
            return unknown;
        int index = RegisterFlowUtil.GpIndex(instruction.MemoryIndex);
        if (index < 0 || !values[index].HasBound || values[index].Upper >= (ulong)(int.MaxValue / size))
            return unknown;
        ulong start = instruction.MemoryDisplacement64;
        if (instruction.MemoryBase != Register.None)
        {
            int register = RegisterFlowUtil.GpIndex(instruction.MemoryBase);
            if (register < 0 || !values[register].HasConstant)
                return unknown;
            start = unchecked(start + values[register].Constant);
        }
        int resultBits = instruction.Op0Kind == OpKind.Register ? instruction.Op0Register.GetSize() * 8 : _binary.is32Bit ? 32 : 64;
        return unknown with { ExtendedBits = resultBits == 32 ? 32 : size * 8, Signed = signed && resultBits == 64,
            Table = new TableAccess(start, checked((int)values[index].Upper + 1), size, signed, resultBits) };
    }

    private static Value Constant(ulong value, int bits) => new(true, bits == 32 ? (uint)value : value, bits, false);

    private static bool IsImmediate(OpKind kind) => kind is OpKind.Immediate8 or OpKind.Immediate8to16
        or OpKind.Immediate8to32 or OpKind.Immediate8to64 or OpKind.Immediate16 or OpKind.Immediate32
        or OpKind.Immediate32to64 or OpKind.Immediate64;

    private static bool IsNearBranch(in Instruction instruction) => instruction.Op0Kind is OpKind.NearBranch16
        or OpKind.NearBranch32 or OpKind.NearBranch64;

    private void Finish()
    {
        Instructions = _decoded.Values.ToList();
        foreach (List<ulong> targets in _successors.Values)
            foreach (ulong target in targets)
                _incomingCounts[target] = _incomingCounts.GetValueOrDefault(target) + 1;
        NormalizeRegions();
        _dataRanges.Sort((left, right) => left.SourceSite.CompareTo(right.SourceSite));
        List<Partition> partitions = new();
        List<UnknownRange> unknown = new();
        foreach (Region region in _regions)
        {
            ByteCount += (long)(region.End - region.Start);
            SortedSet<ulong> boundaries = new() { region.Start, region.End };
            foreach (Instruction instruction in Instructions)
            {
                if (instruction.IP < region.End && instruction.NextIP > region.Start)
                {
                    boundaries.Add(Math.Max(instruction.IP, region.Start));
                    boundaries.Add(Math.Min(instruction.NextIP, region.End));
                }
            }
            foreach (DataRange data in _dataRanges)
            {
                if (data.Start < region.End && data.End > region.Start)
                {
                    boundaries.Add(Math.Max(data.Start, region.Start));
                    boundaries.Add(Math.Min(data.End, region.End));
                }
            }
            ulong[] points = boundaries.ToArray();
            for (int index = 0; index + 1 < points.Length; index++)
            {
                ulong start = points[index], end = points[index + 1];
                long raw = region.RawOffset < 0 ? -1 : region.RawOffset + (long)(start - region.Start);
                ContentKind kind = OverlapsCode(start, end) ? ContentKind.Code
                    : IsData(start) ? ContentKind.Data : ContentKind.Unknown;
                string reason = kind == ContentKind.Unknown
                    ? _issues.FirstOrDefault(issue => issue.Address >= start && issue.Address < end).Reason
                        ?? "not reached from a proven code entry" : null;
                partitions.Add(new Partition(start, end, raw, kind, reason));
                if (kind == ContentKind.Unknown)
                    unknown.Add(new UnknownRange(start, end, raw, reason));
            }
        }
        Partitions = partitions;
        UnknownRanges = unknown;
        Status = _regions.Count == 0 ? "unmapped"
            : _boundaryKnown && unknown.Count == 0 && _issues.Count == 0 ? "complete" : "partial";
    }

    private void NormalizeRegions()
    {
        SourceRanges = _regions.Select(region => new SourceRange(region.Start, region.End, region.RawOffset,
            region.Source, region.Executable)).ToArray();
        SortedSet<ulong> boundaries = new();
        foreach (Region region in _regions)
        {
            boundaries.Add(region.Start);
            boundaries.Add(region.End);
            boundaries.Add(region.Start + (ulong)region.Bytes.Length);
        }
        List<Region> physical = new();
        ulong[] points = boundaries.ToArray();
        for (int index = 0; index + 1 < points.Length; index++)
        {
            ulong start = points[index], end = points[index + 1];
            Region[] sources = _regions.Where(region => start >= region.Start && end <= region.End).ToArray();
            if (sources.Length == 0)
                continue;
            Region backing = sources.FirstOrDefault(region => end <= region.Start + (ulong)region.Bytes.Length);
            long raw = backing == null ? -1 : backing.RawOffset + (long)(start - backing.Start);
            byte[] bytes = backing == null ? Array.Empty<byte>()
                : start == backing.Start && end == backing.Start + (ulong)backing.Bytes.Length ? backing.Bytes
                : backing.Bytes.AsSpan((int)(start - backing.Start), (int)(end - start)).ToArray();
            string source = string.Join("; ", sources.Select(region => region.Source).Distinct());
            physical.Add(new Region(start, end, raw, bytes, source, sources.Any(region => region.Executable)));
        }
        _regions.Clear();
        for (int first = 0; first < physical.Count;)
        {
            int next = first + 1;
            while (next < physical.Count)
            {
                Region previous = physical[next - 1], current = physical[next];
                if (previous.End != current.Start || previous.Executable != current.Executable
                    || previous.RawOffset < 0 || current.RawOffset < 0
                    || previous.Bytes.LongLength != (long)(previous.End - previous.Start)
                    || current.Bytes.LongLength != (long)(current.End - current.Start)
                    || previous.RawOffset + previous.Bytes.LongLength != current.RawOffset)
                    break;
                next++;
            }
            Region region = physical[first];
            if (next > first + 1)
            {
                ulong end = physical[next - 1].End;
                byte[] bytes = new byte[checked((int)(end - region.Start))];
                int offset = 0;
                for (int index = first; index < next; index++)
                {
                    physical[index].Bytes.CopyTo(bytes, offset);
                    offset += physical[index].Bytes.Length;
                }
                string source = string.Join("; ", physical.Skip(first).Take(next - first).Select(value => value.Source).Distinct());
                region = region with { End = end, Bytes = bytes, Source = source };
            }
            _regions.Add(region);
            first = next;
        }
    }
}
