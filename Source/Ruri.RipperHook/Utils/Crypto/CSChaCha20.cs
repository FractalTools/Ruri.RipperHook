/*
 * Copyright (c) 2015, 2018 Scott Bennett
 *           (c) 2018-2023 Kaarlo Räihä
 *
 * Permission to use, copy, modify, and distribute this software for any
 * purpose with or without fee is hereby granted, provided that the above
 * copyright notice and this permission notice appear in all copies.
 *
 * THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
 * WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
 * ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
 * WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
 * ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
 * OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
 */

using System.Runtime.CompilerServices;

namespace Ruri.RipperHook.Crypto;

public sealed class CSChaCha20 : IDisposable
{
    public const int allowedKeyLength = 32;
    public const int allowedNonceLength = 12;
    public const int processBytesAtTime = 64;
    private const int stateLength = 16;

    private readonly uint[] state = new uint[stateLength];
    private bool isDisposed = false;

    private static readonly byte[] sigma = "expand 32-byte k"u8.ToArray();

    public CSChaCha20(byte[] key, byte[] nonce, uint counter)
    {
        KeySetup(key);
        IvSetup(nonce, counter);
    }

    private void KeySetup(byte[] key)
    {
        if (key == null || key.Length != allowedKeyLength)
            throw new ArgumentException($"Key length must be {allowedKeyLength}");

        state[4] = U8To32Little(key, 0);
        state[5] = U8To32Little(key, 4);
        state[6] = U8To32Little(key, 8);
        state[7] = U8To32Little(key, 12);

        int keyIndex = key.Length - 16;
        state[8] = U8To32Little(key, keyIndex);
        state[9] = U8To32Little(key, keyIndex + 4);
        state[10] = U8To32Little(key, keyIndex + 8);
        state[11] = U8To32Little(key, keyIndex + 12);

        state[0] = U8To32Little(sigma, 0);
        state[1] = U8To32Little(sigma, 4);
        state[2] = U8To32Little(sigma, 8);
        state[3] = U8To32Little(sigma, 12);
    }

    private void IvSetup(byte[] nonce, uint counter)
    {
        if (nonce == null || nonce.Length != allowedNonceLength)
            throw new ArgumentException($"Nonce length must be {allowedNonceLength}");

        state[12] = counter;
        state[13] = U8To32Little(nonce, 0);
        state[14] = U8To32Little(nonce, 4);
        state[15] = U8To32Little(nonce, 8);
    }

    public byte[] DecryptBytes(byte[] input)
    {
        if (isDisposed) throw new ObjectDisposedException(nameof(CSChaCha20));
        byte[] output = new byte[input.Length];
        WorkBytes(output, input, input.Length);
        return output;
    }

    public void DecryptStream(Stream output, Stream input, int bufferSize = 4096)
    {
        if (isDisposed) throw new ObjectDisposedException(nameof(CSChaCha20));
        byte[] inputBuf = new byte[bufferSize];
        byte[] outputBuf = new byte[bufferSize];
        int read;
        while ((read = input.Read(inputBuf, 0, bufferSize)) > 0)
        {
            WorkBytes(outputBuf, inputBuf, read);
            output.Write(outputBuf, 0, read);
        }
    }

    private void WorkBytes(byte[] output, byte[] input, int numBytes)
    {
        uint[] x = new uint[stateLength];
        byte[] tmp = new byte[processBytesAtTime];
        int offset = 0;

        int fullLoops = numBytes / processBytesAtTime;
        int tail = numBytes - fullLoops * processBytesAtTime;

        for (int loop = 0; loop < fullLoops; loop++)
        {
            GenerateBlock(state, x, tmp);
            for (int i = 0; i < processBytesAtTime; i++)
                output[offset + i] = (byte)(input[offset + i] ^ tmp[i]);
            offset += processBytesAtTime;
        }

        if (tail > 0)
        {
            GenerateBlock(state, x, tmp);
            for (int i = 0; i < tail; i++)
                output[offset + i] = (byte)(input[offset + i] ^ tmp[i]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GenerateBlock(uint[] s, uint[] x, byte[] tmp)
    {
        Buffer.BlockCopy(s, 0, x, 0, stateLength * sizeof(uint));

        for (int i = 0; i < 10; i++)
        {
            QR(x, 0, 4, 8, 12); QR(x, 1, 5, 9, 13);
            QR(x, 2, 6, 10, 14); QR(x, 3, 7, 11, 15);
            QR(x, 0, 5, 10, 15); QR(x, 1, 6, 11, 12);
            QR(x, 2, 7, 8, 13); QR(x, 3, 4, 9, 14);
        }

        for (int i = 0; i < stateLength; i++)
            ToBytes(tmp, unchecked(x[i] + s[i]), 4 * i);

        s[12] = unchecked(s[12] + 1);
        if (s[12] == 0) s[13] = unchecked(s[13] + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QR(uint[] x, uint a, uint b, uint c, uint d)
    {
        unchecked
        {
            x[a] += x[b]; x[d] = RotL(x[d] ^ x[a], 16);
            x[c] += x[d]; x[b] = RotL(x[b] ^ x[c], 12);
            x[a] += x[b]; x[d] = RotL(x[d] ^ x[a], 8);
            x[c] += x[d]; x[b] = RotL(x[b] ^ x[c], 7);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotL(uint v, int c) => unchecked(v << c | v >> (32 - c));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint U8To32Little(byte[] p, int o) =>
        unchecked(p[o] | (uint)p[o + 1] << 8 | (uint)p[o + 2] << 16 | (uint)p[o + 3] << 24);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ToBytes(byte[] buf, uint val, int o)
    {
        unchecked
        {
            buf[o] = (byte)val;
            buf[o + 1] = (byte)(val >> 8);
            buf[o + 2] = (byte)(val >> 16);
            buf[o + 3] = (byte)(val >> 24);
        }
    }

    public void Dispose()
    {
        if (!isDisposed)
        {
            Array.Clear(state, 0, stateLength);
            isDisposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~CSChaCha20() => Dispose();
}
