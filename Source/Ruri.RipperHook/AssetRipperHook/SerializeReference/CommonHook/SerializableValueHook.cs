using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Primitives;
using AssetRipper.SerializationLogic;

namespace Ruri.RipperHook.AR;

public partial class AR_SerializeReference_Hook
{
    private const string RegistryTypeName = "ManagedReferencesRegistry";
    private const string RegistryFieldName = "references";

    [RetargetMethod(typeof(SerializableValue), nameof(SerializableValue.Read))]
    public static bool Read(
        ref SerializableValue self,
        ref EndianSpanReader reader,
        UnityVersion version,
        TransferInstructionFlags flags,
        int depth,
        in SerializableType.Field etalon)
    {
        if (etalon.Type.Name is not RegistryTypeName || etalon.Name is not RegistryFieldName)
        {
            return false;
        }
        if (SerializeReferenceContext.ReadingReferencedObject)
        {
            return true;
        }
        self = new SerializableValue(0, ManagedReferencesRegistry.Read(
            ref reader, version, flags, depth,
            SerializeReferenceContext.RefTypes,
            SerializeReferenceContext.AssemblyManager));
        if (etalon.Align)
        {
            reader.Align();
        }
        return true;
    }
}
