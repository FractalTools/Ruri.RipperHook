namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// One field of a material's uniform buffer that a preshader fills: where it sits in the
/// buffer, how many 32-bit lanes it spans, and the preshader program (a range of the
/// material's opcode data, and which of that program's fields it is) that computes it from
/// the material's numeric parameters -- so it can be computed again for any parameter values.
/// <see cref="Program"/> is that program spelled out, for reading; <see cref="Parameters"/>
/// names every parameter it reads.
/// </summary>
public readonly record struct PreshaderField(string Member, int Offset, int Rows, uint OpcodeOffset, uint OpcodeSize, int FieldSlot, int NumFields, string? Program, IReadOnlyList<string> Parameters);
