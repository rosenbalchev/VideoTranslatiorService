using RB.VideoTranslator.Data.Entities;

namespace RB.VideoTranslator.BLL.Services;

public sealed class VoiceSynthesiserService : IVoiceSynthesiserService
{
    public Task SynthesiseAsync(VideoJob job, CancellationToken ct = default) =>
        throw new StepNotImplementedException(nameof(IVoiceSynthesiserService));
}
