using Microsoft.EntityFrameworkCore;
using RdtClient.Data.Models.Data;

namespace RdtClient.Data.Data;

public interface IRejectedTorrentData
{
    Task<RejectedTorrent?> Get(String hash);
    Task AddOrUpdate(String hash, String reason);
    Task DeleteOlderThan(DateTimeOffset dateTime);
}

public class RejectedTorrentData(DataContext dataContext) : IRejectedTorrentData
{
    public async Task<RejectedTorrent?> Get(String hash)
    {
        hash = hash.ToLowerInvariant();

        return await dataContext.RejectedTorrents.AsNoTracking().FirstOrDefaultAsync(m => m.Hash == hash);
    }

    public async Task AddOrUpdate(String hash, String reason)
    {
        hash = hash.ToLowerInvariant();

        var rejectedTorrent = await dataContext.RejectedTorrents.FirstOrDefaultAsync(m => m.Hash == hash);

        if (rejectedTorrent == null)
        {
            rejectedTorrent = new()
            {
                Hash = hash
            };

            await dataContext.RejectedTorrents.AddAsync(rejectedTorrent);
        }

        rejectedTorrent.Reason = reason;
        rejectedTorrent.Rejected = DateTimeOffset.UtcNow;

        await dataContext.SaveChangesAsync();
    }

    public async Task DeleteOlderThan(DateTimeOffset dateTime)
    {
        var rejectedTorrents = await dataContext.RejectedTorrents.ToListAsync();

        var expired = rejectedTorrents.Where(m => m.Rejected < dateTime).ToList();

        if (expired.Count == 0)
        {
            return;
        }

        dataContext.RejectedTorrents.RemoveRange(expired);

        await dataContext.SaveChangesAsync();
    }
}
