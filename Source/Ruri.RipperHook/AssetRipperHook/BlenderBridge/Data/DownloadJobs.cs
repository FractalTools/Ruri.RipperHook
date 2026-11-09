using System.Security.Cryptography;
using System.Text.Json;
using Ruri.RipperHook.BlenderBridge.Data;
using Ruri.RipperHook.BlenderBridge.Tables;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>Bounded streaming transfers and small, memory-only receipts. Explicit destinations
/// remain caller-owned; source-created temporary results are deleted when their receipt is forgotten
/// or evicted. Producers must finish all writes before returning.</summary>
public static class DownloadJobs
{
    public const int MaximumActive = 4;
    public const int MaximumReceipts = 64;
    private const string JobsId = "core.jobs";
    private const string ControlId = "core.jobs.control";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Job> Jobs = new(StringComparer.Ordinal);
    private static long sequence;

    private sealed class Job(string owner, string destination, string metadata, bool ownsResult)
    {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly string Owner = owner;
        internal readonly string Destination = destination;
        internal readonly string Metadata = metadata;
        internal readonly bool OwnsResult = ownsResult;
        internal readonly long Sequence = Interlocked.Increment(ref sequence);
        internal readonly CancellationTokenSource Cancellation = new();
        internal CancellationTokenRegistration StartupRegistration;
        internal int StartupRegistrationDisposed;
        internal Task? CancellationCallbacks;
        internal bool CommitStarted;
        internal bool Finalizing;
        internal bool Forgetting;
        internal string State = "queued";
        internal long Received;
        internal long Total = -1;
        internal string ErrorCode = string.Empty;
        internal string ErrorMessage = string.Empty;
        internal int RetryAfter = -1;
        internal string ResultPath = string.Empty;
        internal long Size;
        internal string Sha256 = string.Empty;
        internal bool Terminal => State is "succeeded" or "failed" or "cancelled";
    }

    internal static void Register()
    {
        Datasets.Publish(JobsId, DataRole.Session, [DataParam.Text("id", required: false)],
            "Current streaming transfers and bounded terminal receipts; no downloaded bytes are retained.",
            Table, cache: false, readerIndependent: true);
        Datasets.PublishBlob(ControlId, DataRole.Session, [],
            "Cancel a transfer or forget its terminal receipt. Forget removes source-owned temporary results; explicit destinations remain untouched.", Control, readerIndependent: true);
    }

    internal static void Unregister()
    {
        Datasets.Remove(JobsId);
        Datasets.Remove(ControlId);
    }

    public static string Start(string owner, string destination,
        Func<Stream, IProgress<TransferProgress>, CancellationToken, Task> producer,
        string metadata = "", bool ownsResult = false, CancellationToken startupCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(producer);
        if (metadata.Length > 16384) throw new ArgumentException("Transfer metadata exceeds 16KiB.", nameof(metadata));
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128
            || owner.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            throw new SourceRequestException("invalid_owner", "A transfer requires a source identifier.");
        string path = DestinationOf(destination);
        Job job = new(owner, path, metadata, ownsResult);
        job.StartupRegistration = startupCancellation.Register(static state =>
        {
            Job candidate = (Job)state!;
            lock (Gate) Cancel(candidate);
        }, job);
        bool receiptRegistered = false;
        try
        {
            while (true)
            {
                Job? oldest = null;
                lock (Gate)
                {
                    if (Jobs.Values.Count(entry => !entry.Terminal) >= MaximumActive)
                        throw new SourceRequestException("transfer_limit", "The concurrent download limit has been reached.");
                    if (Jobs.Values.Any(entry => !entry.Terminal
                        && string.Equals(entry.Destination, path, StringComparison.OrdinalIgnoreCase)))
                        throw new SourceRequestException("destination_busy", "Another transfer is using that destination.");
                    if (Jobs.Count < MaximumReceipts)
                    {
                        Jobs.Add(job.Id, job);
                        receiptRegistered = true;
                        break;
                    }
                    oldest = Jobs.Values.Where(entry => entry.Terminal && !entry.Forgetting)
                        .MinBy(entry => entry.Sequence);
                    if (oldest is null)
                        throw new SourceRequestException("transfer_limit", "The transfer receipt limit has been reached.");
                    oldest.Forgetting = true;
                }
                try
                {
                    DeleteOwnedArtifacts(oldest);
                    lock (Gate) Jobs.Remove(oldest.Id);
                }
                catch
                {
                    lock (Gate) oldest.Forgetting = false;
                    throw;
                }
            }
        }
        catch
        {
            DisposeStartupRegistration(job);
            if (!receiptRegistered)
            {
                Task? callbacks;
                lock (Gate) callbacks = job.CancellationCallbacks;
                _ = DisposeCancellation(job.Cancellation, callbacks);
            }
            throw;
        }
        _ = Task.Run(() => Run(job, producer));
        return job.Id;
    }

    /// <summary>Signal cancellation asynchronously. Provider teardown never waits for a worker or
    /// invokes its cancellation callbacks inline while holding the provider's authentication lock.</summary>
    public static void CancelOwner(string owner)
    {
        lock (Gate)
        {
            foreach (Job job in Jobs.Values.Where(job => string.Equals(job.Owner, owner, StringComparison.Ordinal)))
                Cancel(job);
        }
    }

    private static string DestinationOf(string destination)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
                throw new SourceRequestException("invalid_destination", "Select an absolute output filename.");
            string path = Path.GetFullPath(destination);
            if (string.IsNullOrEmpty(Path.GetFileName(path)) || !Directory.Exists(Path.GetDirectoryName(path)))
                throw new SourceRequestException("invalid_destination", "The selected output folder must already exist.");
            if (File.Exists(path) || Directory.Exists(path))
                throw new SourceRequestException("destination_exists", "The destination already exists; select another filename.");
            return path;
        }
        catch (SourceRequestException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException
                                           or UnauthorizedAccessException)
        {
            throw new SourceRequestException("invalid_destination", "The selected output filename is invalid or inaccessible.");
        }
    }

    private static async Task Run(Job job,
        Func<Stream, IProgress<TransferProgress>, CancellationToken, Task> producer)
    {
        string partial = Path.Combine(Path.GetDirectoryName(job.Destination)!, $".ruri-{job.Id}.partial");
        bool ownsPartial = false;
        string? terminalState = null;
        try
        {
            CancellationToken token = job.Cancellation.Token;
            lock (Gate)
            {
                token.ThrowIfCancellationRequested();
                job.State = "running";
            }
            string sha256;
            long size;
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (FileStream file = new(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    ownsPartial = true;
                    TransferOutput output = new(file, hash, job, token);
                    await producer(output, new TransferReporter(job), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    await file.FlushAsync(token).ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                    size = output.Written;
                    sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }
            }
            lock (Gate)
            {
                // Commit wins or cancellation wins here. Keep filesystem operations outside
                // the registry lock, including for destinations on slow network filesystems.
                token.ThrowIfCancellationRequested();
                if (job.Total >= 0 && job.Total != size)
                    throw new SourceRequestException("incomplete_download", "The received size does not match the source length.");
                job.CommitStarted = true;
            }
            if (File.Exists(job.Destination) || Directory.Exists(job.Destination))
                throw new SourceRequestException("destination_exists", "The destination was created by another operation.");
            File.Move(partial, job.Destination, overwrite: false);
            ownsPartial = false;
            lock (Gate)
            {
                job.Received = size;
                job.ResultPath = job.Destination;
                job.Size = size;
                job.Sha256 = sha256;
                job.State = "succeeded";
            }
        }
        catch (Exception exception)
        {
            lock (Gate)
            {
                // Choose the outcome now, but publish it only after owned-partial cleanup.
                // Forget and eviction must not erase a pending cleanup error.
                job.Finalizing = true;
                if (job.Cancellation.IsCancellationRequested)
                {
                    terminalState = "cancelled";
                    job.ErrorCode = "cancelled";
                    job.ErrorMessage = "The download was cancelled.";
                }
                else
                {
                    terminalState = "failed";
                    (job.ErrorCode, job.ErrorMessage, job.RetryAfter) = PublicError(exception);
                }
            }
        }
        finally
        {
            if (ownsPartial)
            {
                try { File.Delete(partial); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    lock (Gate)
                    {
                        job.ErrorCode = "partial_cleanup_failed";
                        job.ErrorMessage = "The transfer stopped, but its temporary output could not be removed.";
                    }
                }
            }
            // CancelAsync can still be dispatching provider callbacks. Disposal is deferred until
            // those callbacks finish; neither receipt eviction nor provider teardown waits on it.
            Task? callbacks;
            lock (Gate)
            {
                if (terminalState is not null) job.State = terminalState;
                callbacks = job.CancellationCallbacks;
            }
            DisposeStartupRegistration(job);
            _ = DisposeCancellation(job.Cancellation, callbacks);
        }
    }

    private static async Task DisposeCancellation(CancellationTokenSource cancellation, Task? callbacks)
    {
        try
        {
            if (callbacks is not null) await callbacks.ConfigureAwait(false);
        }
        catch (Exception) { /* Callback failures are not public source errors. */ }
        finally { cancellation.Dispose(); }
    }

    // Gate must be held. Terminal jobs have committed or completed their cleanup decision and
    // their cancellation sources may already be disposed, so cancelling one is a harmless no-op.
    private static void Cancel(Job job)
    {
        if (!job.Terminal && !job.CommitStarted && !job.Finalizing && job.CancellationCallbacks is null)
            job.CancellationCallbacks = job.Cancellation.CancelAsync();
    }

    private static (string Code, string Message, int RetryAfter) PublicError(Exception exception)
    {
        if (exception is SourceRequestException source)
        {
            string code = source.Code;
            if (string.IsNullOrWhiteSpace(code) || code.Length > 64
                || code.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
                code = "source_error";
            string message = source.Message;
            if (message.Length is 0 or > 240 || message.Contains("://", StringComparison.Ordinal)
                || message.Contains('\r') || message.Contains('\n'))
                message = "The source request failed.";
            return (code, message, Math.Max(-1, source.RetryAfterSeconds ?? -1));
        }
        return exception switch
        {
            UnauthorizedAccessException => ("destination_access", "The output file is not writable.", -1),
            IOException => ("output_io", "The download output could not be written or committed.", -1),
            OperationCanceledException => ("request_timeout", "The source request timed out.", -1),
            _ => ("download_failed", "The download failed.", -1),
        };
    }

    private static ColumnTable Table(DataRequest request)
    {
        request.Cancellation.ThrowIfCancellationRequested();
        string id = request.Text("id");
        TableBuilder table = new(JobsId, "id", "owner", "state", "received#", "total#", "error_code",
            "error_message", "retry_after#", "result_path", "size#", "sha256", "metadata", "owned_result#");
        table.Role(ColumnRole.Key, "id").Role(ColumnRole.Label, "owner");
        lock (Gate)
        {
            foreach (Job job in Jobs.Values.Where(job => id.Length == 0 || job.Id == id).OrderBy(job => job.Sequence))
                table.Row(job.Id, job.Owner, job.State, job.Received, job.Total, job.ErrorCode,
                    job.ErrorMessage, job.RetryAfter, job.ResultPath, job.Size, job.Sha256, job.Metadata,
                    job.OwnsResult);
        }
        return table.Build();
    }

    private static byte[] Control(DataRequest request)
    {
        request.Cancellation.ThrowIfCancellationRequested();
        (string action, string id) = ControlPayload.Read(request.Payload, "id");
        Job job;
        lock (Gate)
        {
            if (!Jobs.TryGetValue(id, out Job? found))
                throw new SourceRequestException("job_not_found", "The transfer receipt is no longer present.");
            job = found;
            switch (action)
            {
                case "cancel": Cancel(job); break;
                case "forget":
                    if (!job.Terminal)
                        throw new SourceRequestException("job_active", "Cancel the transfer and wait for completion before forgetting it.");
                    if (job.Forgetting)
                        throw new SourceRequestException("job_active", "The transfer receipt is already being forgotten.");
                    job.Forgetting = true;
                    break;
                default:
                    throw new SourceRequestException("invalid_action", "The transfer action must be cancel or forget.");
            }
        }
        if (action == "forget")
        {
            try
            {
                DeleteOwnedArtifacts(job);
                lock (Gate) Jobs.Remove(id);
            }
            catch
            {
                lock (Gate) job.Forgetting = false;
                throw;
            }
        }
        return JsonSerializer.SerializeToUtf8Bytes(new { id, state = job.State, forgotten = action == "forget" });
    }

    private static void DeleteOwnedArtifacts(Job job)
    {
        DisposeStartupRegistration(job);
        // The partial name is deterministic even when the first cleanup attempt failed before
        // ResultPath was published. Forget/eviction must retry it before dropping the receipt.
        DeleteOwnedFile(Path.Combine(Path.GetDirectoryName(job.Destination)!, $".ruri-{job.Id}.partial"),
            "partial_cleanup_failed", "The transfer temporary output could not be removed.");
        if (job.OwnsResult && job.ResultPath.Length > 0)
            DeleteOwnedFile(job.ResultPath, "destination_cleanup", "The temporary transfer result could not be removed.");
    }

    private static void DeleteOwnedFile(string path, string ioCode, string message)
    {
        try { File.Delete(path); }
        catch (UnauthorizedAccessException)
        { throw new SourceRequestException("destination_access", message); }
        catch (IOException)
        { throw new SourceRequestException(ioCode, message); }
    }

    private static void DisposeStartupRegistration(Job job)
    {
        if (Interlocked.Exchange(ref job.StartupRegistrationDisposed, 1) == 0)
            job.StartupRegistration.Dispose();
    }

    private sealed class TransferReporter(Job job) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            lock (Gate)
            {
                if (!job.Terminal && !job.CommitStarted && !job.Finalizing)
                    job.Total = value.Total < 0 ? -1 : value.Total;
            }
        }
    }

    /// <summary>Write-only, non-seekable borrowed output. Receipt bytes and hash are measured
    /// from successful writes, not trusted producer progress. The worker owns file disposal.</summary>
    private sealed class TransferOutput(Stream output, IncrementalHash hash, Job job, CancellationToken cancellation) : Stream
    {
        private readonly SemaphoreSlim writes = new(1, 1);
        internal long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            writes.Wait(cancellation);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                output.Write(buffer);
                Observe(buffer);
            }
            finally { writes.Release(); }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.ThrowIfCancellationRequested();
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, cancellationToken);
            await writes.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await output.WriteAsync(buffer, linked.Token).ConfigureAwait(false);
                Observe(buffer.Span);
            }
            finally { writes.Release(); }
        }
        private void Observe(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            Written += buffer.Length;
            lock (Gate) job.Received = Written;
        }
        protected override void Dispose(bool disposing) { /* The worker owns output and hash. */ }
    }
}
