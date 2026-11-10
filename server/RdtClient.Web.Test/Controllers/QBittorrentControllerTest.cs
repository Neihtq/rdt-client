using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using RdtClient.Data.Data;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.QBittorrent;
using RdtClient.Service.Services;
using RdtClient.Web.Controllers;

namespace RdtClient.Web.Test.Controllers;

public class QBittorrentControllerTest
{
    private readonly QBittorrentController _controller;
    private readonly Mock<QBittorrent> _qBittorrentMock;
    private readonly TestSettings _settings;
    private readonly Mock<Torrents> _torrentsMock;
    private readonly FakeRejectedTorrentData _rejectedTorrentData = new();

    public QBittorrentControllerTest()
    {
        _settings = new();
        _qBittorrentMock = new(new Mock<ILogger<QBittorrent>>().Object, _settings, null!, null!, null!, new TorrentRunnerState());
        _torrentsMock = new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, _settings, new TorrentRunnerState());

        _controller = new(new Mock<ILogger<QBittorrentController>>().Object,
                          _qBittorrentMock.Object,
                          new Mock<IHttpClientFactory>().Object,
                          _settings,
                          _torrentsMock.Object,
                          new(Mock.Of<ILogger<RejectedTorrents>>(), _rejectedTorrentData, _settings));

        _controller.ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext()
        };
    }

    [Fact]
    public async Task TorrentsInfo_FilterAll_DoesNotFilterOutResults()
    {
        // Arrange
        _qBittorrentMock.Setup(q => q.TorrentInfo())
                        .ReturnsAsync(new List<TorrentInfo>
                        {
                            new()
                            {
                                Hash = "hash1",
                                State = "pausedUP",
                                Progress = 1f
                            }
                        });

        // Act
        var result = await _controller.TorrentsInfo(new()
        {
            Filter = "all",
            Hashes = "hash1"
        });

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsAssignableFrom<IList<TorrentInfo>>(okResult.Value);
        Assert.Single(payload);
        Assert.Equal("hash1", payload[0].Hash);
    }

    [Fact]
    public async Task TorrentsInfo_FilterCompleted_MatchesPausedUploadTorrents()
    {
        // Arrange
        _qBittorrentMock.Setup(q => q.TorrentInfo())
                        .ReturnsAsync(new List<TorrentInfo>
                        {
                            new()
                            {
                                Hash = "hash1",
                                State = "pausedUP",
                                Progress = 1f
                            },
                            new()
                            {
                                Hash = "hash2",
                                State = "downloading",
                                Progress = 0.4f
                            }
                        });

        // Act
        var result = await _controller.TorrentsInfo(new()
        {
            Filter = "completed"
        });

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsAssignableFrom<IList<TorrentInfo>>(okResult.Value);
        Assert.Single(payload);
        Assert.Equal("hash1", payload[0].Hash);
    }

    private const String Hash = "0123456789abcdef0123456789abcdef01234567";
    private const String Magnet = "magnet:?xt=urn:btih:" + Hash + "&dn=Some.Show.S01E01";

    private Torrent SetupAdd(TorrentStatus status, String? error)
    {
        var torrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = Hash,
            RdStatus = status,
            Error = error
        };

        _qBittorrentMock.Setup(q => q.TorrentsAddMagnet(Magnet, "sonarr", null)).ReturnsAsync(torrent);
        _torrentsMock.Setup(t => t.GetById(torrent.TorrentId)).ReturnsAsync(torrent);
        _torrentsMock.Setup(t => t.GetByHash(Hash)).ReturnsAsync((Torrent?) null);
        _torrentsMock.Setup(t => t.Delete(torrent.TorrentId, true, true, false)).Returns(Task.CompletedTask);

        return torrent;
    }

    [Fact]
    public async Task TorrentsAdd_Accepted_ReturnsOk()
    {
        SetupAdd(TorrentStatus.Downloading, null);

        var result = await _controller.TorrentsAdd(new()
        {
            Urls = Magnet,
            Category = "sonarr"
        });

        Assert.IsType<OkResult>(result);
        _torrentsMock.Verify(t => t.Delete(It.IsAny<Guid>(), It.IsAny<Boolean>(), It.IsAny<Boolean>(), It.IsAny<Boolean>()), Times.Never);
    }

    [Fact]
    public async Task TorrentsAdd_PermanentError_ReturnsConflictDeletesAndRemembers()
    {
        var torrent = SetupAdd(TorrentStatus.Queued, "Could not add to provider: Infringing file");

        var result = await _controller.TorrentsAdd(new()
        {
            Urls = Magnet,
            Category = "sonarr"
        });

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
        _torrentsMock.Verify(t => t.Delete(torrent.TorrentId, true, true, false), Times.Once);
        Assert.True(_rejectedTorrentData.Items.ContainsKey(Hash));
    }

    [Fact]
    public async Task TorrentsAdd_TemporaryError_ReturnsFailsAndDeletes()
    {
        var torrent = SetupAdd(TorrentStatus.Queued, "Could not add to provider: Service unavailable");

        var result = await _controller.TorrentsAdd(new()
        {
            Urls = Magnet,
            Category = "sonarr"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Fails.", ok.Value);
        _torrentsMock.Verify(t => t.Delete(torrent.TorrentId, true, true, false), Times.Once);
        Assert.Empty(_rejectedTorrentData.Items);
    }

    [Fact]
    public async Task TorrentsAdd_PreviouslyRejected_ReturnsConflictWithoutAdding()
    {
        await _rejectedTorrentData.AddOrUpdate(Hash.ToUpperInvariant(), "Infringing file");

        var result = await _controller.TorrentsAdd(new()
        {
            Urls = Magnet,
            Category = "sonarr"
        });

        Assert.IsType<ConflictObjectResult>(result);
        _qBittorrentMock.Verify(q => q.TorrentsAddMagnet(It.IsAny<String>(), It.IsAny<String?>(), It.IsAny<Int32?>()), Times.Never);
    }

    [Fact]
    public async Task TorrentsAdd_PreviouslyRejectedButExpired_AddsAgain()
    {
        await _rejectedTorrentData.AddOrUpdate(Hash, "Infringing file");
        _rejectedTorrentData.Items[Hash].Rejected = DateTimeOffset.UtcNow.AddHours(-_settings.Current.Integrations.Rejections.RememberHours - 1);
        SetupAdd(TorrentStatus.Downloading, null);

        var result = await _controller.TorrentsAdd(new()
        {
            Urls = Magnet,
            Category = "sonarr"
        });

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task TorrentsAdd_RememberDisabled_DoesNotRemember()
    {
        _settings.Current.Integrations.Rejections.RememberHours = 0;
        SetupAdd(TorrentStatus.Queued, "Could not add to provider: Infringing file");

        var result = await _controller.TorrentsAdd(new()
        {
            Urls = Magnet,
            Category = "sonarr"
        });

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Empty(_rejectedTorrentData.Items);
    }

    [Fact]
    public async Task TorrentsAdd_AddThrows_ReturnsConflict()
    {
        _qBittorrentMock.Setup(q => q.TorrentsAddMagnet(Magnet, "sonarr", null)).ThrowsAsync(new("Cannot add torrent, the torrent contains banned trackers"));

        var result = await _controller.TorrentsAdd(new()
        {
            Urls = Magnet,
            Category = "sonarr"
        });

        Assert.IsType<ConflictObjectResult>(result);
    }

    private sealed class FakeRejectedTorrentData : IRejectedTorrentData
    {
        public Dictionary<String, RejectedTorrent> Items { get; } = [];

        public Task<RejectedTorrent?> Get(String hash)
        {
            return Task.FromResult(Items.GetValueOrDefault(hash.ToLowerInvariant()));
        }

        public Task AddOrUpdate(String hash, String reason)
        {
            Items[hash.ToLowerInvariant()] = new()
            {
                Hash = hash.ToLowerInvariant(),
                Reason = reason,
                Rejected = DateTimeOffset.UtcNow
            };

            return Task.CompletedTask;
        }

        public Task DeleteOlderThan(DateTimeOffset dateTime)
        {
            foreach (var item in Items.Values.Where(m => m.Rejected < dateTime).ToList())
            {
                Items.Remove(item.Hash);
            }

            return Task.CompletedTask;
        }
    }
}
