using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;

namespace RdtClient.Service.Services;

/// <summary>
///     Tells Sonarr/Radarr/Lidarr when a torrent they added fails or stalls on the debrid provider.
///     Their qBittorrent integration never reports a torrent as failed, so without this they wait forever.
///     The release is removed from the *arr queue with blocklist=true, which makes the *arr search for another release.
/// </summary>
public class ArrNotifier(ILogger<ArrNotifier> logger, IHttpClientFactory httpClientFactory, ISettings settings)
{
    public static readonly TimeSpan ErrorGracePeriod = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<String, (Int64 Progress, DateTimeOffset Changed)> _progress = new();
    private readonly ConcurrentDictionary<String, DateTimeOffset> _nextAttempt = new();
    private readonly ConcurrentDictionary<String, Boolean> _notified = new();

    public async Task Check(IList<Torrent> torrents, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var arrSettings = settings.Current.Integrations.Arr;

        if (!arrSettings.Enabled)
        {
            return;
        }

        var instances = GetInstances();

        if (instances.Count == 0)
        {
            return;
        }

        var activeHashes = torrents.Select(m => m.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var hash in _progress.Keys.Where(m => !activeHashes.Contains(m)))
        {
            _progress.TryRemove(hash, out _);
        }

        foreach (var hash in _nextAttempt.Keys.Where(m => !activeHashes.Contains(m)))
        {
            _nextAttempt.TryRemove(hash, out _);
        }

        foreach (var hash in _notified.Keys.Where(m => !activeHashes.Contains(m)))
        {
            _notified.TryRemove(hash, out _);
        }

        var failedTorrents = new List<(Torrent Torrent, ArrInstance Instance, String Reason)>();

        foreach (var torrent in torrents)
        {
            var instance = instances.FirstOrDefault(m => m.Categories.Contains(torrent.Category ?? ""));

            if (instance == null || _notified.ContainsKey(torrent.Hash))
            {
                continue;
            }

            var reason = GetFailureReason(torrent, now);

            if (reason == null)
            {
                continue;
            }

            if (_nextAttempt.TryGetValue(torrent.Hash, out var nextAttempt) && nextAttempt > now)
            {
                continue;
            }

            failedTorrents.Add((torrent, instance, reason));
        }

        foreach (var group in failedTorrents.GroupBy(m => m.Instance))
        {
            IList<ArrQueueItem> queue;

            try
            {
                queue = await GetQueue(group.Key, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not get the queue from {arr}: {message}", group.Key.Name, ex.Message);

                foreach (var (torrent, _, _) in group)
                {
                    _nextAttempt[torrent.Hash] = now + RetryInterval;
                }

                continue;
            }

            foreach (var (torrent, instance, reason) in group)
            {
                var queueItem = queue.FirstOrDefault(m => String.Equals(m.DownloadId, torrent.Hash, StringComparison.OrdinalIgnoreCase));

                if (queueItem == null)
                {
                    logger.LogDebug("Torrent {hash} ({name}) failed but is not in the {arr} queue", torrent.Hash, torrent.RdName, instance.Name);
                    _nextAttempt[torrent.Hash] = now + RetryInterval;

                    continue;
                }

                try
                {
                    logger.LogInformation("Torrent {name} ({hash}) failed: {reason}. Removing it from {arr} and blocklisting the release",
                                          torrent.RdName,
                                          torrent.Hash,
                                          reason,
                                          instance.Name);

                    await BlocklistQueueItem(instance, queueItem.Id, cancellationToken);

                    _notified[torrent.Hash] = true;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is { } statusCode && (Int32) statusCode is >= 400 and < 500)
                {
                    // E.g. Sonarr refuses to blocklist releases it has no grab history for, retrying will not help.
                    logger.LogWarning("{arr} refused to blocklist {name} ({statusCode}), not trying again", instance.Name, torrent.RdName, (Int32) statusCode);
                    _notified[torrent.Hash] = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not blocklist {name} in {arr}: {message}", torrent.RdName, instance.Name, ex.Message);
                    _nextAttempt[torrent.Hash] = now + RetryInterval;
                }
            }
        }
    }

    public String? GetFailureReason(Torrent torrent, DateTimeOffset now)
    {
        var arrSettings = settings.Current.Integrations.Arr;

        if (torrent.Error != null)
        {
            _progress.TryRemove(torrent.Hash, out _);

            if (!arrSettings.NotifyOnError || torrent.Retry != null)
            {
                return null;
            }

            // Wait a bit so the qBittorrent add endpoint can handle errors on add itself.
            if (torrent.Completed != null && torrent.Completed.Value + ErrorGracePeriod > now)
            {
                return null;
            }

            // Errors while downloading to the host are not a problem of the release.
            if (torrent.RdStatus != TorrentStatus.Error && torrent.Downloads.Count > 0)
            {
                return null;
            }

            return torrent.Error;
        }

        if (arrSettings.StallTimeout <= 0 ||
            torrent.Completed != null ||
            torrent.RdStatus is not (TorrentStatus.Processing or TorrentStatus.Downloading or TorrentStatus.WaitingForFileSelection) ||
            String.Equals(torrent.RdStatusRaw, "queued", StringComparison.OrdinalIgnoreCase))
        {
            _progress.TryRemove(torrent.Hash, out _);

            return null;
        }

        var progress = torrent.RdProgress ?? 0;

        var current = _progress.AddOrUpdate(torrent.Hash,
                                            _ => (progress, now),
                                            (_, existing) => existing.Progress == progress ? existing : (progress, now));

        var stalledFor = now - current.Changed;

        if (stalledFor < TimeSpan.FromMinutes(arrSettings.StallTimeout))
        {
            return null;
        }

        return $"Stalled at {progress}% for {(Int32) stalledFor.TotalMinutes} minutes";
    }

    private List<ArrInstance> GetInstances()
    {
        var arrSettings = settings.Current.Integrations.Arr;

        var instances = new List<ArrInstance?>
        {
            ArrInstance.Create("Sonarr", arrSettings.SonarrUrl, arrSettings.SonarrApiKey, "v3", arrSettings.SonarrCategories),
            ArrInstance.Create("Radarr", arrSettings.RadarrUrl, arrSettings.RadarrApiKey, "v3", arrSettings.RadarrCategories),
            ArrInstance.Create("Lidarr", arrSettings.LidarrUrl, arrSettings.LidarrApiKey, "v1", arrSettings.LidarrCategories)
        };

        return instances.OfType<ArrInstance>().ToList();
    }

    private async Task<IList<ArrQueueItem>> GetQueue(ArrInstance instance, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, instance, "queue/details");
        using var response = await CreateClient().SendAsync(request, cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<ArrQueueItem>>(cancellationToken) ?? [];
    }

    private async Task BlocklistQueueItem(ArrInstance instance, Int64 queueId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, instance, $"queue/{queueId}?removeFromClient=true&blocklist=true&skipRedownload=false");
        using var response = await CreateClient().SendAsync(request, cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    private HttpClient CreateClient()
    {
        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        return httpClient;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, ArrInstance instance, String path)
    {
        var request = new HttpRequestMessage(method, $"{instance.Url}/api/{instance.ApiVersion}/{path}");
        request.Headers.Add("X-Api-Key", instance.ApiKey);

        return request;
    }

    public sealed record ArrInstance(String Name, String Url, String ApiKey, String ApiVersion, HashSet<String> Categories)
    {
        public static ArrInstance? Create(String name, String? url, String? apiKey, String apiVersion, String? categories)
        {
            if (String.IsNullOrWhiteSpace(url) || String.IsNullOrWhiteSpace(apiKey) || String.IsNullOrWhiteSpace(categories))
            {
                return null;
            }

            var categorySet = categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return new(name, url.Trim().TrimEnd('/'), apiKey.Trim(), apiVersion, categorySet);
        }

        public Boolean Equals(ArrInstance? other)
        {
            return other != null && Name == other.Name;
        }

        public override Int32 GetHashCode()
        {
            return Name.GetHashCode();
        }
    }

    public class ArrQueueItem
    {
        [JsonPropertyName("id")]
        public Int64 Id { get; set; }

        [JsonPropertyName("downloadId")]
        public String? DownloadId { get; set; }
    }
}
