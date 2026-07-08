using Microsoft.EntityFrameworkCore;
using RB.VideoTranslator.Data.Context;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Data.Repositories;

public sealed class VoicePaceRepository : IVoicePaceRepository
{
    private readonly AppDbContext _db;

    public VoicePaceRepository(AppDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<string, VoicePaceStat>> GetAllAsync(CancellationToken ct = default) =>
        await _db.VoicePaceStats.AsNoTracking().ToDictionaryAsync(s => s.Voice, ct);

    public async Task RecordSampleAsync(string voice, int textLength, double rate, int actualMs, CancellationToken ct = default)
    {
        if (textLength <= 0 || actualMs <= 0 || rate <= 0) return;

        var stat = await _db.VoicePaceStats.FindAsync([voice], ct);
        if (stat is null)
        {
            stat = new VoicePaceStat { Voice = voice };
            _db.VoicePaceStats.Add(stat);
        }

        stat.TotalChars     += textLength;
        stat.TotalNaturalMs += actualMs * rate / 100.0;
        stat.SampleCount++;
        stat.LastRateUsed = rate;
        stat.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
    }
}
