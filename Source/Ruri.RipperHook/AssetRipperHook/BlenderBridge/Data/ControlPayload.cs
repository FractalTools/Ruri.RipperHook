using System.Text.Json;

namespace Ruri.RipperHook.BlenderBridge.Data;

internal static class ControlPayload
{
    internal static (string Action, string Id) Read(ReadOnlyMemory<byte> payload, string idProperty)
    {
        if (payload.Length is 0 or > 4096)
            throw Invalid();
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("action", out JsonElement action)
                || !root.TryGetProperty(idProperty, out JsonElement id)
                || action.ValueKind != JsonValueKind.String || id.ValueKind != JsonValueKind.String)
                throw Invalid();
            string actionText = action.GetString() ?? string.Empty;
            string idText = id.GetString() ?? string.Empty;
            if (actionText.Length is 0 or > 32 || idText.Length is 0 or > 128)
                throw Invalid();
            return (actionText, idText);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    private static SourceRequestException Invalid() =>
        new("invalid_request", "The control payload requires an action and an identifier.");
}
