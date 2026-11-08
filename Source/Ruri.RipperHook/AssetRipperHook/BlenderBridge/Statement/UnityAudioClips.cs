using AssetRipper.Export.Modules.Audio;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Extensions;
using Fmod5Sharp;
using Fmod5Sharp.FmodTypes;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// An audio clip as the engine plays one. The conversion to a standard Ogg Vorbis file is the importer's own
/// (<see cref="AudioClipDecoder"/>). What it does not give back is read from the same bytes: the channels, the frequency and the
/// whole number of samples per channel live only in the header of the clip's sound bank -- the clip's serialized fields carry
/// its length as a single-precision number of seconds -- and the game's own timeline takes a clip's duration as its sample
/// count over its frequency, so that is what is stated.
/// </summary>
public static class UnityAudioClips
{
    public static StatementAudioClip? Restate(IAudioClip clip, string key, out string reason)
    {
        if (!AudioClipDecoder.TryDecode(clip, out byte[]? ogg, out string? extension, out string? message))
        {
            reason = message ?? string.Empty;
            return null;
        }
        if (extension != "ogg")
        {
            reason = $"it is stored as {extension}, and the engine plays Ogg Vorbis only";
            return null;
        }
        if (!FsbLoader.TryLoadFsbFromByteArray(clip.GetAudioData(), out FmodSoundBank? bank) || bank is null)
        {
            reason = "it is not held in a sound bank, so no header states its sample count";
            return null;
        }
        if (bank.Samples.Count != 1)
        {
            reason = $"its sound bank holds {bank.Samples.Count} sounds, not one";
            return null;
        }
        FmodSampleMetadata header = bank.Samples[0].Metadata;
        if (header.Channels == 0 || header.Frequency <= 0 || header.SampleCount == 0)
        {
            reason = $"its header states {header.Channels} channel(s), {header.Frequency} Hz and {header.SampleCount} sample(s)";
            return null;
        }
        reason = string.Empty;
        return new StatementAudioClip(key, clip.Name.String, (int)header.Channels, header.Frequency, header.SampleCount,
            (double)header.SampleCount / (double)header.Frequency, clip.Length, clip.CompressionFormatE.ToString(), ogg, clip.Collection.Name);
    }
}
