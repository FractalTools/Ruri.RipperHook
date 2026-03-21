using System.Reflection;
using AssetRipper.Primitives;
using AssetsTools.NET;
using Ruri.RipperHook.Core;
using USCSandbox.Processor;
using MonoModHook = MonoMod.RuntimeDetour.Hook;

namespace Ruri.RipperHook.Endfield;

/// <summary>
/// Runtime patches for USCSandbox limitations:
/// - ConstantBuffer(AssetTypeValueField, Dictionary) throws NotSupportedException for struct params
/// - ShaderSubProgram skips inline ShaderParams for Unity >= 2021 (version hardcode bug)
/// - TextureParameter.Index packed format: low 16 bits = bind point, high 16 bits = flags
/// </summary>
public partial class EndField_1_1_9_Hook
{
    private static readonly List<MonoModHook> _uscsSandboxHooks = new();

    public void ApplyUSCSandboxFixes()
    {
        try
        {
            HookConstantBufferCtor();
            HookBlobManagerGetShaderSubProgram();
            HookBlobManagerGetShaderParams();
            USCSandboxRenderStateHook.Install();
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[AR_ShaderDecompiler] USCSandbox fixes: {ex.Message}");
        }
    }

    private void HookConstantBufferCtor()
    {
        var ctor = typeof(ConstantBuffer).GetConstructor(new[] { typeof(AssetTypeValueField), typeof(Dictionary<int, string>) });
        if (ctor == null) return;

        _uscsSandboxHooks.Add(new MonoModHook(ctor,
            (Action<Action<ConstantBuffer, AssetTypeValueField, Dictionary<int, string>>,
                ConstantBuffer, AssetTypeValueField, Dictionary<int, string>>)
            ((orig, self, field, nameTable) =>
            {
                self.Name = nameTable[field["m_NameIndex"].AsInt];
                self.UsedSize = field["m_Size"].AsInt;
                self.Partial = field["m_IsPartialCB"].AsBool;
                self.CBParams = new List<ConstantBufferParameter>();

                foreach (var p in field["m_MatrixParams.Array"])
                    self.CBParams.Add(new ConstantBufferParameter(p, nameTable));
                foreach (var p in field["m_VectorParams.Array"])
                    self.CBParams.Add(new ConstantBufferParameter(p, nameTable));

                // Skip struct params — not critical for DXBC decompilation
                self.StructParams = new List<StructParameter>();
            })
        ));
        HookLogger.LogSuccessRaw("    [+] Hooked ConstantBuffer.ctor (struct params fix)");
    }

    /// <summary>
    /// Fix: USCSandbox ShaderSubProgram.ctor uses version.IsLess(2021) to decide whether
    /// to read inline ShaderParams. For Unity 2021+, ShaderParams is skipped → texture
    /// parameters are null → "Could not find texture parameter" warnings.
    ///
    /// Cannot hook ShaderSubProgram.ctor directly — MonoMod DMD fails because the ctor
    /// calls IL-patched methods (IsGreaterEqual/IsLess added by PrimitivesPatcher).
    /// Instead, hook BlobManager.GetShaderSubProgram which calls the ctor internally.
    /// After the ctor returns, read remaining ShaderParams data if present.
    /// </summary>
    private void HookBlobManagerGetShaderSubProgram()
    {
        var method = typeof(BlobManager).GetMethod("GetShaderSubProgram");
        if (method == null) return;

        var engVerField = typeof(BlobManager).GetField("_engVer", BindingFlags.NonPublic | BindingFlags.Instance);
        if (engVerField == null) return;

        _uscsSandboxHooks.Add(new MonoModHook(method,
            (Func<Func<BlobManager, int, ShaderSubProgram>, BlobManager, int, ShaderSubProgram>)
            ((orig, self, index) =>
            {
                // Re-implement to access the reader position after ctor completes
                var blobEntry = self.GetRawEntry(index);
                var r = new AssetsFileReader(new MemoryStream(blobEntry));
                var engVer = (UnityVersion)engVerField.GetValue(self);
                var result = new ShaderSubProgram(r, engVer);

                // Fix: read inline ShaderParams for Unity 2021+ if data remains
                if (result.ShaderParams == null && r.Position < r.BaseStream.Length)
                {
                    result.ShaderParams = new ShaderParams(r, engVer, false);
                }
                return result;
            })
        ));
        HookLogger.LogSuccessRaw("    [+] Hooked BlobManager.GetShaderSubProgram (inline ShaderParams fix for 2021+)");
    }

    /// <summary>
    /// Fix: TextureParameter.Index in param blobs uses packed format where the actual
    /// register bind point is in the low 16 bits and flags/metadata in the high 16 bits.
    /// USCSandbox reads it as a plain int32 → USILSamplerMetadder can't match register
    /// indices → "Could not find texture parameter for resource rsc0" warnings.
    /// </summary>
    private void HookBlobManagerGetShaderParams()
    {
        var method = typeof(BlobManager).GetMethod("GetShaderParams");
        if (method == null) return;

        _uscsSandboxHooks.Add(new MonoModHook(method,
            (Func<Func<BlobManager, int, ShaderParams>, BlobManager, int, ShaderParams>)
            ((orig, self, index) =>
            {
                var result = orig(self, index);

                // Fix packed TextureParameter.Index — extract actual bind point from low 16 bits
                foreach (var tp in result.TextureParameters)
                    tp.Index &= 0xFFFF;

                return result;
            })
        ));
        HookLogger.LogSuccessRaw("    [+] Hooked BlobManager.GetShaderParams (TextureParameter.Index fix)");
    }
}
