using AssetRipper.Assets.Bundles;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.CabMapping;

internal static class SerializedFileOpener
{
    public static int Visit(string file, Action<string, SerializedFile> visit, Action<string, Exception> failed)
    {
        List<FileBase> fileStack = new();

        try
        {
            GameBundleHook.FilePreInitializeDelegate? preInitialize = GameBundleHook.CustomFilePreInitialize;
            if (preInitialize is not null)
            {
                preInitialize(new GameBundle(), new[] { file }, fileStack, LocalFileSystem.Instance, null);
            }
            else
            {
                fileStack.Add(SchemeReader.LoadFile(file, LocalFileSystem.Instance));
            }
        }
        catch (Exception ex)
        {
            failed("Unpack", ex);
            return 0;
        }

        int visited = 0;
        string fallbackName = Path.GetFileName(file);
        foreach (FileBase fileBase in fileStack)
        {
            // WHICH ARCHIVE this came out of, not which file was opened to get at it. A
            // decoder whose archives carry other archives puts each of them on the stack
            // under its own name, and naming them all after the file on disk threw that
            // away -- so a closure could only ever say "open this container", never "open
            // this archive out of it", and one wanted archive dragged in every tenant of
            // its container. The file on disk is kept separately (Entry.RelativePath) and
            // is still what gets opened; this is the name the load is gated by.
            string archiveName = fileBase.Name is { Length: > 0 } ? fileBase.Name : fallbackName;
            try
            {
                IEnumerable<SerializedFile> serializedFiles;
                if (fileBase is SerializedFile single)
                {
                    serializedFiles = [single];
                }
                else if (fileBase is FileContainer container)
                {
                    container.ReadContentsRecursively();
                    serializedFiles = container.FetchSerializedFiles();
                }
                else
                {
                    continue;
                }

                foreach (SerializedFile sf in serializedFiles)
                {
                    visit(archiveName, sf);
                    visited++;
                }
            }
            catch (Exception ex)
            {
                failed("Read", ex);
            }
            finally
            {
                (fileBase as IDisposable)?.Dispose();
            }
        }

        return visited;
    }
}
