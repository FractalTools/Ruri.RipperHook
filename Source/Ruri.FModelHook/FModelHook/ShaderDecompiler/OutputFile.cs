namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// Writing one of a run's output files on a machine that is also watching them.
///
/// A pass writes hundreds of thousands of small files across every core, and on Windows a file
/// created that way is opened moments later by whatever indexes, scans or reads the folder. For
/// that moment the write is refused with a sharing violation -- a condition that passes on its
/// own, and that used to take the whole run down with it because one refused variant faulted the
/// task that was emitting its map.
///
/// So a refusal of exactly that kind is waited out, briefly and a bounded number of times. Every
/// file this writes is named after what is in it, so the bytes a retry writes are the bytes the
/// first attempt meant to write. Anything else -- a full disk, a missing folder, a denied path --
/// is not waited on at all, and a holder that never lets go still stops the run.
/// </summary>
internal static class OutputFile
{
    private const int Attempts = 8;

    private const int SharingViolation = 32;

    private const int LockViolation = 33;

    public static void Write(string path, string text)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllText(path, text);
                return;
            }
            catch (IOException refusal) when (attempt < Attempts && IsHeldForAMoment(refusal))
            {
                Thread.Sleep(attempt * 20);
            }
        }
    }

    private static bool IsHeldForAMoment(IOException refusal)
        => (refusal.HResult & 0xFFFF) is SharingViolation or LockViolation;
}
