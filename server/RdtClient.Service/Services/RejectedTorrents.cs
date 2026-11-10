using Microsoft.Extensions.Logging;
using MonoTorrent;
using RdtClient.Data.Data;
using RdtClient.Data.Models.Data;

namespace RdtClient.Service.Services;

/// <summary>
///     Keeps track of releases the debrid provider refused permanently (e.g. "Infringing file"), so that
///     the *arr apps get an immediate rejection when they try to add the same release again.
/// </summary>
public class RejectedTorrents(ILogger<RejectedTorrents> logger, IRejectedTorrentData rejectedTorrentData, ISettings settings)
{
    public static String? GetHash(String magnetLink)
    {
        try
        {
            return MagnetLink.Parse(magnetLink).InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    public static async Task<String?> GetHash(Byte[] torrentFile)
    {
        try
        {
            var torrent = await MonoTorrent.Torrent.LoadAsync(torrentFile);

            return torrent.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    public static Boolean IsPermanentError(String? error, String? permanentErrors)
    {
        if (String.IsNullOrWhiteSpace(error) || String.IsNullOrWhiteSpace(permanentErrors))
        {
            return false;
        }

        return permanentErrors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .Any(m => error.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    public Boolean IsPermanentError(String? error)
    {
        return IsPermanentError(error, settings.Current.Integrations.Rejections.PermanentErrors);
    }

    public virtual async Task<RejectedTorrent?> Get(String hash)
    {
        var rememberHours = settings.Current.Integrations.Rejections.RememberHours;

        if (rememberHours <= 0)
        {
            return null;
        }

        var rejectedTorrent = await rejectedTorrentData.Get(hash);

        if (rejectedTorrent == null || rejectedTorrent.Rejected.AddHours(rememberHours) < DateTimeOffset.UtcNow)
        {
            return null;
        }

        return rejectedTorrent;
    }

    public virtual async Task Add(String hash, String reason)
    {
        if (settings.Current.Integrations.Rejections.RememberHours <= 0)
        {
            return;
        }

        logger.LogInformation("Remembering {hash} as rejected: {reason}", hash, reason);

        await rejectedTorrentData.AddOrUpdate(hash, reason);
    }

    public async Task Cleanup()
    {
        var rememberHours = Math.Max(settings.Current.Integrations.Rejections.RememberHours, 0);

        await rejectedTorrentData.DeleteOlderThan(DateTimeOffset.UtcNow.AddHours(-rememberHours));
    }
}
