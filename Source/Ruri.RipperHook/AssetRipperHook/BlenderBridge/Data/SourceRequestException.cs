namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>An intentionally public, sanitized source error. Never pass response bodies,
/// credentials, signed URLs, or an underlying exception message to this constructor.</summary>
public class SourceRequestException(string code, string message, int? httpStatus = null,
    int? retryAfterSeconds = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>A streaming transfer observation. Total is -1 when the source gives no length.</summary>
public readonly record struct TransferProgress(long Received, long Total);
