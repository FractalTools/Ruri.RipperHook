using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.IO.Objects;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// A seekable read-only view of ONE file inside a mounted container, which fetches only the
/// stretches actually read.
///
/// A shader archive is the largest thing a title ships -- gigabyte scale -- and a caller after
/// one character wants a few megabytes of it: the tables at the front, then the handful of
/// shaders its maps own. The container reader can serve a byte range (that is how bulk data is
/// paged in), so a range is what this asks for, decrypting and decompressing only the blocks
/// that cover it.
///
/// Reads are served out of fixed-size windows so that walking the tables -- which reads four
/// bytes at a time -- costs one fetch per window rather than one per field, and a scattered
/// read of one shader costs one window rather than the whole archive. A read larger than a
/// window goes straight through as its own range.
///
/// A container whose reader does not honour a range answers with the whole file; that is not an
/// error and not a special case to name a game for -- the whole file is then simply what this
/// holds, and every later read is served from it.
/// </summary>
internal sealed class GameFileWindow : Stream
{
    private const int WindowBytes = 64 * 1024;
    private const int WindowsKept = 64;

    private readonly GameFile file;
    private readonly Dictionary<long, byte[]> windows = new();
    private readonly Queue<long> order = new();
    private byte[]? whole;
    private long position;

    /// <summary>How much of the file was actually fetched, which is what a ranged read is for.</summary>
    public long BytesFetched { get; private set; }

    public GameFileWindow(GameFile file)
    {
        this.file = file ?? throw new ArgumentNullException(nameof(file));
        Length = file.Size;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length { get; }

    public override long Position
    {
        get => position;
        set => position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int wanted = (int)Math.Min(count, Math.Max(0, Length - position));
        if (wanted <= 0)
        {
            return 0;
        }
        if (whole is null && wanted >= WindowBytes)
        {
            byte[] straight = Fetch(position, wanted);
            Buffer.BlockCopy(straight, 0, buffer, offset, wanted);
            position += wanted;
            return wanted;
        }
        int done = 0;
        while (done < wanted)
        {
            byte[] window = Window(position, out int start, out int available);
            int take = Math.Min(available, wanted - done);
            Buffer.BlockCopy(window, start, buffer, offset + done, take);
            done += take;
            position += take;
        }
        return done;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>The window holding <paramref name="at"/>, where in it that byte sits, and how much of the window follows it.</summary>
    private byte[] Window(long at, out int start, out int available)
    {
        if (whole is not null)
        {
            start = (int)at;
            available = whole.Length - start;
            return whole;
        }
        long begin = at / WindowBytes * WindowBytes;
        if (!windows.TryGetValue(begin, out byte[]? window))
        {
            window = Fetch(begin, (int)Math.Min(WindowBytes, Length - begin));
            if (whole is not null)
            {
                return Window(at, out start, out available);
            }
            windows[begin] = window;
            order.Enqueue(begin);
            if (order.Count > WindowsKept)
            {
                windows.Remove(order.Dequeue());
            }
        }
        start = (int)(at - begin);
        available = window.Length - start;
        return window;
    }

    /// <summary>
    /// One range of the file. A reader that answers with the whole file instead has told us it
    /// cannot serve ranges, so the whole file is kept and this returns the asked-for slice of it.
    /// </summary>
    private byte[] Fetch(long offset, int size)
    {
        byte[] data = file.Read(new FByteBulkDataHeader(default, size, (uint)size, offset, FBulkDataCookedIndex.Default));
        BytesFetched += data.LongLength;
        if (data.Length == size)
        {
            return data;
        }
        if (data.LongLength != Length)
        {
            throw new InvalidDataException(
                $"'{file.Path}': asked for {size} byte(s) at {offset} and got {data.LongLength}, which is neither that range nor the whole {Length}-byte file.");
        }
        whole = data;
        windows.Clear();
        order.Clear();
        byte[] slice = new byte[size];
        Buffer.BlockCopy(whole, (int)offset, slice, 0, size);
        return slice;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            windows.Clear();
            order.Clear();
            whole = null;
        }
        base.Dispose(disposing);
    }
}
