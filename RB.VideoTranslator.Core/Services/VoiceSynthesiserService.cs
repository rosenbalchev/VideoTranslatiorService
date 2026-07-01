using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Exceptions;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Core.Services;

public sealed class VoiceSynthesiserService : IVoiceSynthesiserService
{
    public Task SynthesiseAsync(VideoJob job, CancellationToken ct = default) =>
        throw new StepNotImplementedException(nameof(IVoiceSynthesiserService));
}
