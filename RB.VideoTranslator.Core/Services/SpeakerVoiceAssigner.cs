namespace RB.VideoTranslator.Core.Services;

// Assigns each speaker in a job a distinct Azure TTS voice for a target language, based on
// their estimated gender (from the VTT's speaker/gender summary header). Used by
// PipelineOrchestrator to turn its per-language VoiceMap entry into a speaker->voice map
// for VttToAzureTtsService.
internal static class SpeakerVoiceAssigner
{
    /// <summary>
    /// Female-estimated speakers draw from <paramref name="femaleVoices"/>, everyone else
    /// (male or unknown) from <paramref name="maleVoices"/> — unless
    /// <paramref name="defaultToFemale"/> is set, in which case "unknown" gender speakers
    /// draw from <paramref name="femaleVoices"/> too (mirrors the meaning
    /// <c>PipelineOptions.UseFemaleVoice</c> already has as the single-voice fallback when
    /// there's no speaker data at all). Voices are assigned in order of first appearance and
    /// cycle back to the start of that gender's list when there are more same-gender
    /// speakers than distinct voices — speakers never cross into the other gender's list.
    /// </summary>
    public static Dictionary<string, string> Assign(
        IReadOnlyList<(string Label, string Gender)> speakers,
        IReadOnlyList<string> maleVoices,
        IReadOnlyList<string> femaleVoices,
        bool defaultToFemale)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var maleIndex = 0;
        var femaleIndex = 0;

        foreach (var (label, gender) in speakers)
        {
            var isFemale = string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase)
                || (defaultToFemale && !string.Equals(gender, "male", StringComparison.OrdinalIgnoreCase));

            if (isFemale && femaleVoices.Count > 0)
                result[label] = femaleVoices[femaleIndex++ % femaleVoices.Count];
            else if (maleVoices.Count > 0)
                result[label] = maleVoices[maleIndex++ % maleVoices.Count];
            // else: neither pool has any voices — leave this speaker unassigned; caller
            // falls back to the language's single default voice for their cues.
        }

        return result;
    }
}
