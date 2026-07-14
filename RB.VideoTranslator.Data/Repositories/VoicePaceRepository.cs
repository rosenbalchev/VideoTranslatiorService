using Microsoft.EntityFrameworkCore;
using RB.VideoTranslator.Data.Context;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Interfaces;
using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Data.Repositories;

public sealed class VoicePaceRepository : IVoicePaceRepository
{
    private readonly AppDbContext _db;

    public VoicePaceRepository(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<VoicePaceSample>> GetAllSamplesAsync(CancellationToken ct = default) =>
        await _db.VoicePaceSamples.AsNoTracking().ToListAsync(ct);

    public async Task RecordSampleAsync(
        string voice,
        TextFeatures features,
        double rate,
        int expectedMs,
        int actualMs,
        CancellationToken ct = default)
    {
        if (features.CharCount <= 0 || actualMs <= 0 || rate <= 0) return;

        _db.VoicePaceSamples.Add(new VoicePaceSample
        {
            Voice         = voice,
            CharCount     = features.CharCount,
            WordCount     = features.WordCount,
            SentenceCount = features.SentenceCount,
            CommaCount    = features.CommaCount,
            RateUsed      = rate,
            ExpectedMs    = expectedMs,
            ActualMs      = actualMs,
            NaturalMs     = actualMs * rate / 100.0,
            CreatedAt     = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
    }
}
