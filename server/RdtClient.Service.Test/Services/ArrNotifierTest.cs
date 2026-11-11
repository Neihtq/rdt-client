using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Service.Services;

namespace RdtClient.Service.Test.Services;

public class ArrNotifierTest
{
    private const String Hash = "0123456789abcdef0123456789abcdef01234567";

    private readonly TestSettings _settings = new();
    private readonly FakeArrHandler _handler = new();
    private readonly ArrNotifier _notifier;
    private readonly DateTimeOffset _now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    public ArrNotifierTest()
    {
        _settings.Current.Integrations.Arr.Enabled = true;
        _settings.Current.Integrations.Arr.SonarrUrl = "http://sonarr:8989/";
        _settings.Current.Integrations.Arr.SonarrApiKey = "key";

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(m => m.CreateClient(It.IsAny<String>())).Returns(() => new(_handler, false));

        _notifier = new(Mock.Of<ILogger<ArrNotifier>>(), httpClientFactory.Object, _settings);
    }

    private Torrent ErroredTorrent(String category = "sonarr")
    {
        return new()
        {
            Hash = Hash,
            RdName = "Some.Show.S01E01",
            Category = category,
            RdStatus = TorrentStatus.Error,
            RdStatusRaw = "dead",
            Error = "Debrid error: dead.",
            Completed = _now.AddMinutes(-5)
        };
    }

    [Fact]
    public async Task Check_ErroredTorrent_BlocklistsQueueItem()
    {
        _handler.QueueJson = $$"""[{"id": 1, "downloadId": "OTHER"}, {"id": 42, "downloadId": "{{Hash.ToUpperInvariant()}}"}]""";

        await _notifier.Check([ErroredTorrent()], _now);

        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal("GET http://sonarr:8989/api/v3/queue/details", _handler.Requests[0]);
        Assert.Equal("DELETE http://sonarr:8989/api/v3/queue/42?removeFromClient=true&blocklist=true&skipRedownload=false", _handler.Requests[1]);
        Assert.All(_handler.ApiKeys, m => Assert.Equal("key", m));
    }

    [Fact]
    public async Task Check_AlreadyNotified_DoesNotNotifyAgain()
    {
        _handler.QueueJson = $$"""[{"id": 42, "downloadId": "{{Hash}}"}]""";

        await _notifier.Check([ErroredTorrent()], _now);
        await _notifier.Check([ErroredTorrent()], _now.AddHours(1));

        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Check_Disabled_DoesNothing()
    {
        _settings.Current.Integrations.Arr.Enabled = false;

        await _notifier.Check([ErroredTorrent()], _now);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Check_UnknownCategory_DoesNothing()
    {
        await _notifier.Check([ErroredTorrent("manual")], _now);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Check_RadarrCategory_UsesRadarr()
    {
        _settings.Current.Integrations.Arr.RadarrUrl = "http://radarr:7878";
        _settings.Current.Integrations.Arr.RadarrApiKey = "key";
        _handler.QueueJson = $$"""[{"id": 7, "downloadId": "{{Hash}}"}]""";

        await _notifier.Check([ErroredTorrent("radarr")], _now);

        Assert.Equal("DELETE http://radarr:7878/api/v3/queue/7?removeFromClient=true&blocklist=true&skipRedownload=false", _handler.Requests[1]);
    }

    [Fact]
    public async Task Check_LidarrCategory_UsesApiV1()
    {
        _settings.Current.Integrations.Arr.LidarrUrl = "http://lidarr:8686";
        _settings.Current.Integrations.Arr.LidarrApiKey = "key";
        _handler.QueueJson = $$"""[{"id": 7, "downloadId": "{{Hash}}"}]""";

        await _notifier.Check([ErroredTorrent("lidarr")], _now);

        Assert.Equal("GET http://lidarr:8686/api/v1/queue/details", _handler.Requests[0]);
    }

    [Fact]
    public async Task Check_ErrorInGracePeriod_Waits()
    {
        var torrent = ErroredTorrent();
        torrent.Completed = _now.AddSeconds(-10);

        await _notifier.Check([torrent], _now);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Check_TorrentPendingRetry_Waits()
    {
        var torrent = ErroredTorrent();
        torrent.Retry = _now;

        await _notifier.Check([torrent], _now);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Check_HostDownloadError_IsIgnored()
    {
        var torrent = ErroredTorrent();
        torrent.RdStatus = TorrentStatus.Finished;
        torrent.Error = "1/1 downloads failed with errors";
        torrent.Downloads = [new()];

        await _notifier.Check([torrent], _now);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Check_NotInQueue_RetriesLater()
    {
        _handler.QueueJson = "[]";

        await _notifier.Check([ErroredTorrent()], _now);
        await _notifier.Check([ErroredTorrent()], _now.AddMinutes(1));

        Assert.Single(_handler.Requests);

        _handler.QueueJson = $$"""[{"id": 42, "downloadId": "{{Hash}}"}]""";

        await _notifier.Check([ErroredTorrent()], _now + ArrNotifier.RetryInterval);

        Assert.Equal(3, _handler.Requests.Count);
        Assert.StartsWith("DELETE", _handler.Requests[2]);
    }

    [Fact]
    public async Task Check_DeleteRejectedByArr_DoesNotRetry()
    {
        _handler.QueueJson = $$"""[{"id": 42, "downloadId": "{{Hash}}"}]""";
        _handler.DeleteStatusCode = HttpStatusCode.BadRequest;

        await _notifier.Check([ErroredTorrent()], _now);
        await _notifier.Check([ErroredTorrent()], _now + ArrNotifier.RetryInterval);

        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Check_StalledTorrent_BlocklistsAfterTimeout()
    {
        _settings.Current.Integrations.Arr.StallTimeout = 60;
        _handler.QueueJson = $$"""[{"id": 42, "downloadId": "{{Hash}}"}]""";

        Torrent Stalled(Int64 progress)
        {
            return new()
            {
                Hash = Hash,
                Category = "sonarr",
                RdStatus = TorrentStatus.Downloading,
                RdStatusRaw = "downloading",
                RdProgress = progress
            };
        }

        await _notifier.Check([Stalled(10)], _now);
        await _notifier.Check([Stalled(20)], _now.AddMinutes(50));
        await _notifier.Check([Stalled(20)], _now.AddMinutes(100));

        Assert.Empty(_handler.Requests);

        await _notifier.Check([Stalled(20)], _now.AddMinutes(111));

        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Check_ProviderQueuedTorrent_IsNotStalled()
    {
        _settings.Current.Integrations.Arr.StallTimeout = 60;

        var torrent = new Torrent
        {
            Hash = Hash,
            Category = "sonarr",
            RdStatus = TorrentStatus.Downloading,
            RdStatusRaw = "queued",
            RdProgress = 0
        };

        await _notifier.Check([torrent], _now);
        await _notifier.Check([torrent], _now.AddHours(5));

        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("Could not add to provider: Infringing file", true)]
    [InlineData("Debrid error: virus.", true)]
    [InlineData("Could not add to provider: Service unavailable", false)]
    [InlineData(null, false)]
    public void IsPermanentError_MatchesDefaults(String? error, Boolean expected)
    {
        Assert.Equal(expected, RejectedTorrents.IsPermanentError(error, new TestSettings().Current.Integrations.Rejections.PermanentErrors));
    }

    [Theory]
    [InlineData("Scrubs S02E08 1080p WEB H264-CAKES.exe", null, ".exe,.scr", "Scrubs S02E08 1080p WEB H264-CAKES.exe")]
    [InlineData("Scrubs S02E08", "Scrubs S02E08/Scrubs.S02E08.SCR", "exe, scr", "Scrubs S02E08/Scrubs.S02E08.SCR")]
    [InlineData("Scrubs S02E08", "Scrubs S02E08/Scrubs.S02E08.mkv", ".exe,.scr", null)]
    [InlineData("Scrubs S02E08 1080p WEB H264-CAKES.exe", null, "", null)]
    [InlineData("Setup.exe.mkv", null, ".exe", null)]
    public void GetBlockedFile_MatchesExtensions(String rdName, String? filePath, String blockedExtensions, String? expected)
    {
        var torrent = new Torrent
        {
            Hash = Hash,
            RdName = rdName
        };

        if (filePath != null)
        {
            torrent.RdFiles = $"[{{\"Path\":\"{filePath}\",\"Bytes\":1}}]";
        }

        Assert.Equal(expected, RejectedTorrents.GetBlockedFile(torrent, blockedExtensions));
    }

    [Fact]
    public async Task GetHash_MagnetAndFile_ReturnLowercaseHash()
    {
        Assert.Equal(Hash, RejectedTorrents.GetHash($"magnet:?xt=urn:btih:{Hash.ToUpperInvariant()}"));
        Assert.Null(RejectedTorrents.GetHash("magnet:?broken"));
        Assert.Null(await RejectedTorrents.GetHash([1, 2, 3]));
    }

    private sealed class FakeArrHandler : HttpMessageHandler
    {
        public List<String> Requests { get; } = [];
        public List<String> ApiKeys { get; } = [];
        public String QueueJson { get; set; } = "[]";
        public HttpStatusCode DeleteStatusCode { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri}");
            ApiKeys.Add(request.Headers.GetValues("X-Api-Key").Single());

            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(QueueJson)
                });
            }

            return Task.FromResult(new HttpResponseMessage(DeleteStatusCode));
        }
    }
}
