using AssetsTools.NET;
using MonoModHook = MonoMod.RuntimeDetour.Hook;
using System.Globalization;
using System.Reflection;
using USCSandbox.Extras;
using USCSandbox.Processor;

namespace Ruri.RipperHook.AR;

/// <summary>
/// Hooks ShaderProcessor.WritePassState and WritePassRtBlend to restore
/// property name references (e.g. [_ZWrite], [_SrcBlend]) that USCSandbox
/// ignores — it only reads .val fields, never .name fields.
/// </summary>
public static class USCSandboxRenderStateHook
{
    private static FieldInfo? _sbField;
    private static readonly List<MonoModHook> _hooks = new();

    public static void Install()
    {
        _sbField = typeof(ShaderProcessor).GetField("_sb", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ShaderProcessor._sb field not found");

        var writePassState = typeof(ShaderProcessor).GetMethod("WritePassState", BindingFlags.NonPublic | BindingFlags.Instance);
        if (writePassState != null)
        {
            _hooks.Add(new MonoModHook(writePassState, typeof(USCSandboxRenderStateHook).GetMethod(nameof(OnWritePassState), BindingFlags.NonPublic | BindingFlags.Static)!));
            Console.WriteLine("    [+] Hooked ShaderProcessor.WritePassState (render state property refs)");
        }

        var writePassRtBlend = typeof(ShaderProcessor).GetMethod("WritePassRtBlend", BindingFlags.NonPublic | BindingFlags.Instance);
        if (writePassRtBlend != null)
        {
            _hooks.Add(new MonoModHook(writePassRtBlend, typeof(USCSandboxRenderStateHook).GetMethod(nameof(OnWritePassRtBlend), BindingFlags.NonPublic | BindingFlags.Static)!));
            Console.WriteLine("    [+] Hooked ShaderProcessor.WritePassRtBlend (blend property refs)");
        }
    }

    private static StringBuilderIndented GetSb(ShaderProcessor self) =>
        (StringBuilderIndented)_sbField!.GetValue(self)!;

    // --- ATVF helpers: read .name alongside .val ---

    private static string? N(AssetTypeValueField parent, string fieldPath)
    {
        try
        {
            var name = parent[$"{fieldPath}.name"].AsString;
            return !string.IsNullOrEmpty(name) ? name : null;
        }
        catch { return null; }
    }

    private static bool Hn(AssetTypeValueField parent, string fieldPath) => N(parent, fieldPath) != null;

    private static string Sv(AssetTypeValueField parent, string fieldPath, Enum val)
    {
        var n = N(parent, fieldPath);
        return n != null ? $"[{n}]" : val.ToString()!;
    }

    private static string SvInt(AssetTypeValueField parent, string fieldPath)
    {
        var n = N(parent, fieldPath);
        return n != null ? $"[{n}]" : ((int)parent[$"{fieldPath}.val"].AsFloat).ToString();
    }

    private static string SvDec(AssetTypeValueField parent, string fieldPath)
    {
        var n = N(parent, fieldPath);
        return n != null ? $"[{n}]" : parent[$"{fieldPath}.val"].AsFloat.ToString(CultureInfo.InvariantCulture);
    }

    private static bool HasStencilName(AssetTypeValueField state, string prefix) =>
        Hn(state, $"{prefix}.pass") || Hn(state, $"{prefix}.fail")
        || Hn(state, $"{prefix}.zFail") || Hn(state, $"{prefix}.comp");

    // --- Hook targets ---

    private delegate void WritePassStateDelegate(ShaderProcessor self, AssetTypeValueField state);
    private static void OnWritePassState(WritePassStateDelegate orig, ShaderProcessor self, AssetTypeValueField state)
    {
        var sb = GetSb(self);

        // Name & LOD
        sb.AppendLine($"Name \"{state["m_Name"].AsString}\"");
        var lod = state["m_LOD"].AsInt;
        if (lod != 0) sb.AppendLine($"LOD {lod}");

        // RtBlend
        var rtSeparateBlend = state["rtSeparateBlend"].AsBool;
        if (rtSeparateBlend)
        {
            for (var i = 0; i < 8; i++)
                WriteRtBlend(sb, state[$"rtBlend{i}"], i);
        }
        else
        {
            WriteRtBlend(sb, state["rtBlend0"], -1);
        }

        // Read values and names
        var alphaToMask = state["alphaToMask.val"].AsFloat;
        var zClip = (ZClip)(int)state["zClip.val"].AsFloat;
        var zTest = (ZTest)(int)state["zTest.val"].AsFloat;
        var zWrite = (ZWrite)(int)state["zWrite.val"].AsFloat;
        var culling = (CullMode)(int)state["culling.val"].AsFloat;
        var offsetFactor = state["offsetFactor.val"].AsFloat;
        var offsetUnits = state["offsetUnits.val"].AsFloat;

        // AlphaToMask
        var alphaToMaskName = N(state, "alphaToMask");
        if (alphaToMask > 0f || alphaToMaskName != null)
            sb.AppendLine(alphaToMaskName != null ? $"AlphaToMask [{alphaToMaskName}]" : "AlphaToMask On");

        // ZClip
        if (zClip == ZClip.On || Hn(state, "zClip"))
            sb.AppendLine($"ZClip {Sv(state, "zClip", zClip)}");

        // ZTest
        if ((zTest != ZTest.None && zTest != ZTest.LEqual) || Hn(state, "zTest"))
            sb.AppendLine($"ZTest {Sv(state, "zTest", zTest)}");

        // ZWrite
        if (zWrite != ZWrite.On || Hn(state, "zWrite"))
            sb.AppendLine($"ZWrite {Sv(state, "zWrite", zWrite)}");

        // Cull
        if (culling != CullMode.Back || Hn(state, "culling"))
            sb.AppendLine($"Cull {Sv(state, "culling", culling)}");

        // Offset
        if (offsetFactor != 0f || offsetUnits != 0f || Hn(state, "offsetFactor") || Hn(state, "offsetUnits"))
            sb.AppendLine($"Offset {SvDec(state, "offsetFactor")}, {SvDec(state, "offsetUnits")}");

        // Stencil
        WriteStencil(sb, state);

        // Fog
        WriteFog(sb, state);

        // Lighting
        if (state["lighting"].AsBool)
            sb.AppendLine("Lighting On");

        // Tags
        var tags = state["m_Tags"]["tags.Array"];
        if (tags.Children.Count > 0)
        {
            sb.AppendLine("Tags {");
            sb.Indent();
            foreach (var tag in tags)
                sb.AppendLine($"\"{tag["first"].AsString}\"=\"{tag["second"].AsString}\"");
            sb.Unindent();
            sb.AppendLine("}");
        }
    }

    private delegate void WritePassRtBlendDelegate(ShaderProcessor self, AssetTypeValueField rtBlend, int index);
    private static void OnWritePassRtBlend(WritePassRtBlendDelegate orig, ShaderProcessor self, AssetTypeValueField rtBlend, int index)
    {
        WriteRtBlend(GetSb(self), rtBlend, index);
    }

    // --- Implementation ---

    private static void WriteRtBlend(StringBuilderIndented sb, AssetTypeValueField rtBlend, int index)
    {
        var srcBlend = (BlendMode)(int)rtBlend["srcBlend.val"].AsFloat;
        var destBlend = (BlendMode)(int)rtBlend["destBlend.val"].AsFloat;
        var srcBlendAlpha = (BlendMode)(int)rtBlend["srcBlendAlpha.val"].AsFloat;
        var destBlendAlpha = (BlendMode)(int)rtBlend["destBlendAlpha.val"].AsFloat;
        var blendOp = (BlendOp)(int)rtBlend["blendOp.val"].AsFloat;
        var blendOpAlpha = (BlendOp)(int)rtBlend["blendOpAlpha.val"].AsFloat;
        var colMask = (ColorWriteMask)(int)rtBlend["colMask.val"].AsFloat;

        bool hasBlendName = Hn(rtBlend, "srcBlend") || Hn(rtBlend, "destBlend")
            || Hn(rtBlend, "srcBlendAlpha") || Hn(rtBlend, "destBlendAlpha");
        bool hasBlendOpName = Hn(rtBlend, "blendOp") || Hn(rtBlend, "blendOpAlpha");
        bool hasColMaskName = Hn(rtBlend, "colMask");

        // Blend
        if (srcBlend != BlendMode.One || destBlend != BlendMode.Zero
            || srcBlendAlpha != BlendMode.One || destBlendAlpha != BlendMode.Zero
            || hasBlendName)
        {
            sb.Append("");
            sb.AppendNoIndent("Blend ");
            if (index != -1) sb.AppendNoIndent($"{index} ");
            sb.AppendNoIndent($"{Sv(rtBlend, "srcBlend", srcBlend)} {Sv(rtBlend, "destBlend", destBlend)}");
            if (srcBlendAlpha != BlendMode.One || destBlendAlpha != BlendMode.Zero
                || Hn(rtBlend, "srcBlendAlpha") || Hn(rtBlend, "destBlendAlpha"))
            {
                sb.AppendNoIndent($", {Sv(rtBlend, "srcBlendAlpha", srcBlendAlpha)} {Sv(rtBlend, "destBlendAlpha", destBlendAlpha)}");
            }
            sb.AppendNoIndent("\n");
        }

        // BlendOp
        if (blendOp != BlendOp.Add || blendOpAlpha != BlendOp.Add || hasBlendOpName)
        {
            sb.Append("");
            sb.AppendNoIndent("BlendOp ");
            if (index != -1) sb.AppendNoIndent($"{index} ");
            sb.AppendNoIndent(Sv(rtBlend, "blendOp", blendOp));
            if (blendOpAlpha != BlendOp.Add || Hn(rtBlend, "blendOpAlpha"))
                sb.AppendNoIndent($", {Sv(rtBlend, "blendOpAlpha", blendOpAlpha)}");
            sb.AppendNoIndent("\n");
        }

        // ColorMask
        if (colMask != ColorWriteMask.All || hasColMaskName)
        {
            sb.Append("");
            sb.AppendNoIndent("ColorMask ");
            if (hasColMaskName)
            {
                sb.AppendNoIndent($"[{N(rtBlend, "colMask")}]");
            }
            else if (colMask == ColorWriteMask.None)
            {
                sb.AppendNoIndent("0");
            }
            else
            {
                if ((colMask & ColorWriteMask.Red) == ColorWriteMask.Red) sb.AppendNoIndent("R");
                if ((colMask & ColorWriteMask.Green) == ColorWriteMask.Green) sb.AppendNoIndent("G");
                if ((colMask & ColorWriteMask.Blue) == ColorWriteMask.Blue) sb.AppendNoIndent("B");
                if ((colMask & ColorWriteMask.Alpha) == ColorWriteMask.Alpha) sb.AppendNoIndent("A");
            }
            if (index != -1) sb.AppendNoIndent($" {index}");
            sb.AppendNoIndent("\n");
        }
    }

    private static void WriteStencil(StringBuilderIndented sb, AssetTypeValueField state)
    {
        var stencilRef = state["stencilRef.val"].AsFloat;
        var stencilReadMask = state["stencilReadMask.val"].AsFloat;
        var stencilWriteMask = state["stencilWriteMask.val"].AsFloat;

        var stencilOpPass = (StencilOp)(int)state["stencilOp.pass.val"].AsFloat;
        var stencilOpFail = (StencilOp)(int)state["stencilOp.fail.val"].AsFloat;
        var stencilOpZfail = (StencilOp)(int)state["stencilOp.zFail.val"].AsFloat;
        var stencilOpComp = (StencilComp)(int)state["stencilOp.comp.val"].AsFloat;

        var stencilOpFrontPass = (StencilOp)(int)state["stencilOpFront.pass.val"].AsFloat;
        var stencilOpFrontFail = (StencilOp)(int)state["stencilOpFront.fail.val"].AsFloat;
        var stencilOpFrontZfail = (StencilOp)(int)state["stencilOpFront.zFail.val"].AsFloat;
        var stencilOpFrontComp = (StencilComp)(int)state["stencilOpFront.comp.val"].AsFloat;

        var stencilOpBackPass = (StencilOp)(int)state["stencilOpBack.pass.val"].AsFloat;
        var stencilOpBackFail = (StencilOp)(int)state["stencilOpBack.fail.val"].AsFloat;
        var stencilOpBackZfail = (StencilOp)(int)state["stencilOpBack.zFail.val"].AsFloat;
        var stencilOpBackComp = (StencilComp)(int)state["stencilOpBack.comp.val"].AsFloat;

        bool hasNames = Hn(state, "stencilRef") || Hn(state, "stencilReadMask") || Hn(state, "stencilWriteMask")
            || HasStencilName(state, "stencilOp") || HasStencilName(state, "stencilOpFront") || HasStencilName(state, "stencilOpBack");

        bool hasValues = stencilRef != 0.0 || stencilReadMask != 255.0 || stencilWriteMask != 255.0
            || !(stencilOpPass == StencilOp.Keep && stencilOpFail == StencilOp.Keep
                && stencilOpZfail == StencilOp.Keep && stencilOpComp == StencilComp.Always)
            || !(stencilOpFrontPass == StencilOp.Keep && stencilOpFrontFail == StencilOp.Keep
                && stencilOpFrontZfail == StencilOp.Keep && stencilOpFrontComp == StencilComp.Always)
            || !(stencilOpBackPass == StencilOp.Keep && stencilOpBackFail == StencilOp.Keep
                && stencilOpBackZfail == StencilOp.Keep && stencilOpBackComp == StencilComp.Always);

        if (!hasValues && !hasNames) return;

        sb.AppendLine("Stencil {");
        sb.Indent();

        if (stencilRef != 0.0 || Hn(state, "stencilRef"))
            sb.AppendLine($"Ref {SvInt(state, "stencilRef")}");
        if (stencilReadMask != 255.0 || Hn(state, "stencilReadMask"))
            sb.AppendLine($"ReadMask {SvInt(state, "stencilReadMask")}");
        if (stencilWriteMask != 255.0 || Hn(state, "stencilWriteMask"))
            sb.AppendLine($"WriteMask {SvInt(state, "stencilWriteMask")}");

        WriteStencilOps(sb, state, "stencilOp", "",
            stencilOpPass, stencilOpFail, stencilOpZfail, stencilOpComp);
        WriteStencilOps(sb, state, "stencilOpFront", "Front",
            stencilOpFrontPass, stencilOpFrontFail, stencilOpFrontZfail, stencilOpFrontComp);
        WriteStencilOps(sb, state, "stencilOpBack", "Back",
            stencilOpBackPass, stencilOpBackFail, stencilOpBackZfail, stencilOpBackComp);

        sb.Unindent();
        sb.AppendLine("}");
    }

    private static void WriteStencilOps(StringBuilderIndented sb, AssetTypeValueField state,
        string prefix, string suffix, StencilOp pass, StencilOp fail, StencilOp zFail, StencilComp comp)
    {
        if (pass != StencilOp.Keep || fail != StencilOp.Keep || zFail != StencilOp.Keep
            || (comp != StencilComp.Always && comp != StencilComp.Disabled)
            || HasStencilName(state, prefix))
        {
            sb.AppendLine($"Comp{suffix} {Sv(state, $"{prefix}.comp", comp)}");
            sb.AppendLine($"Pass{suffix} {Sv(state, $"{prefix}.pass", pass)}");
            sb.AppendLine($"Fail{suffix} {Sv(state, $"{prefix}.fail", fail)}");
            sb.AppendLine($"ZFail{suffix} {Sv(state, $"{prefix}.zFail", zFail)}");
        }
    }

    private static void WriteFog(StringBuilderIndented sb, AssetTypeValueField state)
    {
        var fogMode = (FogMode)(int)state["fogMode"].AsFloat;
        var fogDensity = state["fogDensity.val"].AsFloat;
        var fogStart = state["fogStart.val"].AsFloat;
        var fogEnd = state["fogEnd.val"].AsFloat;
        var fogColorX = state["fogColor.x.val"].AsFloat;
        var fogColorY = state["fogColor.y.val"].AsFloat;
        var fogColorZ = state["fogColor.z.val"].AsFloat;
        var fogColorW = state["fogColor.w.val"].AsFloat;

        if (fogMode == FogMode.Unknown && fogDensity == 0.0 && fogStart == 0.0 && fogEnd == 0.0
            && fogColorX == 0.0 && fogColorY == 0.0 && fogColorZ == 0.0 && fogColorW == 0.0)
            return;

        sb.AppendLine("Fog {");
        sb.Indent();
        if (fogMode != FogMode.Unknown)
            sb.AppendLine($"Mode {fogMode}");
        if (fogColorX != 0.0 || fogColorY != 0.0 || fogColorZ != 0.0 || fogColorW != 0.0)
        {
            sb.AppendLine($"Color ({fogColorX.ToString(CultureInfo.InvariantCulture)}," +
                $"{fogColorY.ToString(CultureInfo.InvariantCulture)}," +
                $"{fogColorZ.ToString(CultureInfo.InvariantCulture)}," +
                $"{fogColorW.ToString(CultureInfo.InvariantCulture)})");
        }
        if (fogDensity != 0.0)
            sb.AppendLine($"Density {fogDensity.ToString(CultureInfo.InvariantCulture)}");
        if (fogStart != 0.0 || fogEnd != 0.0)
            sb.AppendLine($"Range {fogStart.ToString(CultureInfo.InvariantCulture)}, {fogEnd.ToString(CultureInfo.InvariantCulture)}");
        sb.Unindent();
        sb.AppendLine("}");
    }
}
