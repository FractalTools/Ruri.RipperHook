using System.Runtime.InteropServices;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>
/// The calling thread's denormal handling set to the process default for the length of one answer. A host that
/// flushes denormals on the thread it calls from (Blender does, on its main thread) otherwise corrupts every float
/// vector AssetRipper packs, because the packing is built from denormal float results.
/// </summary>
public readonly struct DefaultDenormalScope : IDisposable
{
    private const uint DenormalMask = 0x03000000;
    private const uint DenormalSave = 0x00000000;

    private readonly uint _previous;
    private readonly bool _changed;

    private DefaultDenormalScope(uint previous)
    {
        _previous = previous;
        _changed = true;
    }

    public static DefaultDenormalScope Enter()
    {
        uint current = Control(0, 0);
        if ((current & DenormalMask) == DenormalSave)
        {
            return default;
        }
        Control(DenormalSave, DenormalMask);
        return new DefaultDenormalScope(current & DenormalMask);
    }

    public void Dispose()
    {
        if (_changed)
        {
            Control(_previous, DenormalMask);
        }
    }

    private static uint Control(uint control, uint mask)
    {
        int result = ControlFloatingPoint(out uint current, control, mask);
        if (result != 0)
        {
            throw new InvalidOperationException($"_controlfp_s failed with error {result}.");
        }
        return current;
    }

    [DllImport("ucrtbase.dll", EntryPoint = "_controlfp_s", ExactSpelling = true)]
    private static extern int ControlFloatingPoint(out uint currentControl, uint newControl, uint mask);
}
