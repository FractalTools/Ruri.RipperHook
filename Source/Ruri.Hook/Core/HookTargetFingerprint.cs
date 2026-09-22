using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

namespace Ruri.Hook.Core
{
    /// <summary>
    /// A stable summary of what an upstream method does, used to notice that upstream rewrote a method a hook replaces.
    /// </summary>
    /// <remarks>
    /// A hook that retargets a method carries a copy of the behaviour that method had when the hook was written. When
    /// upstream later adds a step to it, nothing breaks loudly: the signature still matches, the detour still installs,
    /// and the hook quietly keeps doing the old thing. That has cost real debugging time before, so every retarget is
    /// fingerprinted and compared against a recorded baseline.
    ///
    /// The fingerprint covers the opcode sequence and the names of the members each instruction refers to. It ignores
    /// metadata tokens and branch offsets, which shift whenever anything else in the assembly changes, so rebuilding
    /// upstream without touching the method leaves the fingerprint alone.
    /// </remarks>
    public static class HookTargetFingerprint
    {
        private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodeTable();

        /// <summary>
        /// Whether the assembly declaring the method was built with optimizations.
        /// </summary>
        /// <remarks>
        /// An unoptimized build keeps the nops, the redundant branches and the local roundtrips that the optimizer
        /// removes, so the same source produces a different fingerprint in each configuration. Baselines are therefore
        /// recorded per configuration rather than normalized, which no amount of filtering could do exactly. The flag is
        /// read per assembly because the assemblies a run hooks into are not necessarily built the same way.
        /// </remarks>
        public static string ConfigurationOf(MethodBase method)
        {
            DebuggableAttribute? attribute = method.Module.Assembly.GetCustomAttribute<DebuggableAttribute>();
            return attribute is null || !attribute.IsJITOptimizerDisabled ? "optimized" : "unoptimized";
        }

        /// <summary>
        /// A key identifying the method across builds, e.g. "AssetRipper.IO.Files.BundleFiles.FileStream.BlocksInfo::Read(EndianReader)".
        /// </summary>
        public static string KeyOf(MethodBase method)
        {
            StringBuilder builder = new();
            builder.Append(method.DeclaringType?.FullName ?? "?").Append("::").Append(method.Name).Append('(');
            ParameterInfo[] parameters = method.GetParameters();
            for (int index = 0; index < parameters.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append(parameters[index].ParameterType.Name);
            }
            return builder.Append(')').ToString();
        }

        /// <summary>
        /// Fingerprint the method, or null when its body cannot be read (abstract, extern, or already detoured).
        /// </summary>
        public static string? Compute(MethodBase method)
        {
            byte[]? il;
            try
            {
                il = method.GetMethodBody()?.GetILAsByteArray();
            }
            catch (Exception)
            {
                return null;
            }
            if (il is null)
            {
                return null;
            }

            StringBuilder shape = new();
            Module module = method.Module;
            Type[]? typeArguments = SafeGenericArguments(method.DeclaringType);
            Type[]? methodArguments = method is MethodInfo { IsGenericMethodDefinition: false } info && info.IsGenericMethod
                ? info.GetGenericArguments()
                : null;

            int position = 0;
            while (position < il.Length)
            {
                if (!TryReadOpCode(il, ref position, out OpCode opCode))
                {
                    shape.Append("?;");
                    break;
                }

                int operandStart = position;
                if (!TryAdvanceOperand(il, ref position, opCode.OperandType))
                {
                    shape.Append("?;");
                    break;
                }

                //A nop carries no behaviour. Skipping it costs nothing and keeps the fingerprint steady across
                //compiler updates, which pad differently.
                if (opCode.Value == OpCodes.Nop.Value)
                {
                    continue;
                }
                shape.Append(opCode.Name).Append(' ');

                //Only the member an instruction points at is meaningful. Its token is not: tokens are renumbered
                //whenever anything else in the assembly moves.
                if (IsMemberOperand(opCode.OperandType) && operandStart + 4 <= il.Length)
                {
                    int token = BitConverter.ToInt32(il, operandStart);
                    shape.Append(ResolveMemberName(module, token, typeArguments, methodArguments));
                }
                shape.Append(';');
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(shape.ToString()));
            StringBuilder text = new(16);
            for (int index = 0; index < 8; index++)
            {
                text.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        private static Type[]? SafeGenericArguments(Type? type)
        {
            try
            {
                return type is { IsGenericType: true } ? type.GetGenericArguments() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ResolveMemberName(Module module, int token, Type[]? typeArguments, Type[]? methodArguments)
        {
            try
            {
                MemberInfo? member = module.ResolveMember(token, typeArguments, methodArguments);
                return member is null ? "?" : $"{member.DeclaringType?.Name}.{member.Name}";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static bool TryReadOpCode(byte[] il, ref int position, out OpCode opCode)
        {
            short value = il[position++];
            if (value == 0xFE)
            {
                if (position >= il.Length)
                {
                    opCode = default;
                    return false;
                }
                //Two byte opcodes are stored as negative shorts, which is what OpCode.Value holds.
                value = unchecked((short)(0xFE00 | il[position++]));
            }
            return OpCodesByValue.TryGetValue(value, out opCode);
        }

        private static bool TryAdvanceOperand(byte[] il, ref int position, OperandType operandType)
        {
            int size = operandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => -1,
                _ => 4,
            };

            if (size < 0)
            {
                if (position + 4 > il.Length)
                {
                    return false;
                }
                int count = BitConverter.ToInt32(il, position);
                size = 4 + (count * 4);
            }

            if (position + size > il.Length)
            {
                return false;
            }
            position += size;
            return true;
        }

        private static bool IsMemberOperand(OperandType operandType)
        {
            return operandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok;
        }

        private static Dictionary<short, OpCode> BuildOpCodeTable()
        {
            Dictionary<short, OpCode> table = new();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is OpCode opCode)
                {
                    table[opCode.Value] = opCode;
                }
            }
            return table;
        }
    }
}
