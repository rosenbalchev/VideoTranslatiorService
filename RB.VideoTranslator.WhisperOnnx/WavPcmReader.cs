namespace RB.VideoTranslator.WhisperOnnx;

/// <summary>
/// Minimal 16-bit PCM WAV reader. Callers are expected to have already normalized the
/// input to 16kHz mono (e.g. via ffmpeg) — this does not resample or downmix.
/// </summary>
public static class WavPcmReader
{
    public static float[] ReadMonoSamples(string wavPath)
    {
        using var stream = File.OpenRead(wavPath);
        using var reader = new BinaryReader(stream);

        if (new string(reader.ReadChars(4)) != "RIFF")
            throw new InvalidDataException($"'{wavPath}' is not a RIFF file.");
        reader.ReadUInt32(); // chunk size
        if (new string(reader.ReadChars(4)) != "WAVE")
            throw new InvalidDataException($"'{wavPath}' is not a WAVE file.");

        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? dataBytes = null;

        while (stream.Position < stream.Length)
        {
            string chunkId = new(reader.ReadChars(4));
            uint chunkSize = reader.ReadUInt32();
            long chunkEnd = stream.Position + chunkSize;

            if (chunkId == "fmt ")
            {
                reader.ReadInt16(); // audio format (1 = PCM)
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                reader.ReadInt16(); // block align
                bitsPerSample = reader.ReadInt16();
            }
            else if (chunkId == "data")
            {
                dataBytes = reader.ReadBytes((int)chunkSize);
            }

            stream.Position = chunkEnd + (chunkEnd % 2); // chunks are word-aligned
        }

        if (dataBytes is null)
            throw new InvalidDataException($"'{wavPath}' has no data chunk.");
        if (bitsPerSample != 16)
            throw new InvalidDataException($"'{wavPath}' is {bitsPerSample}-bit PCM; only 16-bit is supported.");
        if (sampleRate != 16000)
            throw new InvalidDataException($"'{wavPath}' is {sampleRate}Hz; expected a pre-normalized 16kHz WAV.");
        if (channels != 1)
            throw new InvalidDataException($"'{wavPath}' has {channels} channels; expected a pre-normalized mono WAV.");

        int sampleCount = dataBytes.Length / 2;
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short raw = (short)(dataBytes[i * 2] | (dataBytes[i * 2 + 1] << 8));
            samples[i] = raw / 32768f;
        }

        return samples;
    }
}
