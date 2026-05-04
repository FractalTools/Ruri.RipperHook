using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using OodleDotNet;

namespace Ruri.RipperHook.Crypto;

public static class OodleHelper
{
    public const string OODLE_DLL_NAME = "oo2core_9_win64.dll"; 
    // Kept the old name as default for compatibility if user manually drops it, 
    // but the download logic might fetch 'oodle-data-shared.dll'. 
    // CUE4Parse uses 'oodle-data-shared.dll' as the new standard name but checks for old one.
    // I will stick to 'oo2core_9_win64.dll' as the target filename for simplicity in this project context unless the zip forces otherwise.
    
    private static Oodle? _instance;

    public static void Initialize(string? path = null)
    {
        if (_instance is not null) return;

        path ??= OODLE_DLL_NAME;
        
        if (!File.Exists(path))
        {
            Console.WriteLine($"Oodle DLL not found at {Path.GetFullPath(path)}. Attempting download...");
            if (!DownloadOodleDll(path))
            {
                throw new FileNotFoundException($"Oodle decompression failed: unable to download oodle dll to {path}");
            }
        }

        try 
        {
            _instance = new Oodle(path);
            Console.WriteLine("Oodle initialized successfully.");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to initialize Oodle from {path}", ex);
        }
    }

    public static int Decompress(Span<byte> compressed, Span<byte> decompressed)
    {
        if (_instance is null)
        {
            Initialize();
        }

        if (_instance is null)
        {
             throw new InvalidOperationException("Oodle decompression failed: not initialized");
        }

        long decodedSize = _instance.Decompress(compressed, decompressed);

        if (decodedSize <= 0)
        {
            throw new IOException($"Oodle decompression failed with result {decodedSize}");
        }

        if (decodedSize < decompressed.Length)
        {
            Console.WriteLine($"Warning: Oodle decompression just decompressed {decodedSize} bytes of the expected {decompressed.Length} bytes");
        }

        return (int)decodedSize;
    }

    private static bool DownloadOodleDll(string path)
    {
        return DownloadOodleDllAsync(path).GetAwaiter().GetResult();
    }

    private static async Task<bool> DownloadOodleDllAsync(string path)
    {
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All
            });
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                "Ruri.RipperHook",
                "1.0.0"));
            client.Timeout = TimeSpan.FromSeconds(60);

            return await DownloadOodleDllFromOodleUEAsync(client, path).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error downloading Oodle DLL: {e.Message}");
            return false;
        }
    }

    private static async Task<bool> DownloadOodleDllFromOodleUEAsync(HttpClient client, string path)
    {
        // URL from CUE4Parse OodleHelper.cs
        const string url = "https://github.com/WorkingRobot/OodleUE/releases/download/2025-07-31-1001/clang-cl.zip"; 
        const string entryName = "bin/Release/oodle-data-shared.dll";

        Console.WriteLine($"Downloading Oodle from {url}...");

        try
        {
            using var response = await client.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            
            await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var zip = new ZipArchive(responseStream, ZipArchiveMode.Read);
            
            var entry = zip.GetEntry(entryName);
            if (entry == null)
            {
                throw new FileNotFoundException($"'{entryName}' not found in the downloaded zip archive.");
            }

            await using var entryStream = entry.Open();
            await using var fs = File.Create(path);
            await entryStream.CopyToAsync(fs).ConfigureAwait(false);
            
            Console.WriteLine($"Downloaded and saved Oodle DLL to {path}");
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Uncaught exception while downloading oodle dll from OodleUE: {e}");
            return false;
        }
    }
}