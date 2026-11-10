using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RdtClient.Data.Data;

namespace RdtClient.Service.Test.Regression;

public class RejectedTorrentDataTests : IAsyncLifetime
{
    private readonly String _databasePath = Path.Combine(Path.GetTempPath(), $"rdt-client-rejected-{Guid.NewGuid():N}.sqlite");

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task AddOrUpdate_GetAndCleanup_Work()
    {
        await using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();

            var data = new RejectedTorrentData(context);

            await data.AddOrUpdate("ABCDEF", "Infringing file");
            await data.AddOrUpdate("abcdef", "Infringing file again");
            await data.AddOrUpdate("123456", "Torrent too big");
        }

        await using (var context = CreateContext())
        {
            var data = new RejectedTorrentData(context);

            var rejected = await data.Get("AbCdEf");
            Assert.NotNull(rejected);
            Assert.Equal("Infringing file again", rejected!.Reason);
            Assert.Equal(2, await context.RejectedTorrents.CountAsync());

            await data.DeleteOlderThan(DateTimeOffset.UtcNow.AddMinutes(1));

            Assert.Equal(0, await context.RejectedTorrents.CountAsync());
        }
    }

    private DataContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DataContext>()
                      .UseSqlite($"Data Source={_databasePath}")
                      .Options;

        return new(options);
    }
}
