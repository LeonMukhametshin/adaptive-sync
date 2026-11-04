using Microsoft.Data.Sqlite;
using Sync.Client.Transport;
using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Time;

namespace Sync.Client.Tests;

public sealed class SyncClientTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeTransport _transport = new();
    private readonly LogicalClock _clock = new();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void Insert_WritesLocalRowAndJournalsOperation()
    {
        using var client = CreateClient(SchemaVersions.V1);

        var id = client.Insert(SchemaVersions.PlayerProfile, new Dictionary<string, object?> { ["nickname"] = "neo", ["coins"] = 10 });

        var record = client.Find(SchemaVersions.PlayerProfile, id)!;
        Assert.Equal("neo", record.Values["nickname"]);
        Assert.Equal(10L, record.Values["coins"]);
        Assert.Equal(1L, record.Values["level"]);
        Assert.Equal(0, record.RowVersion);

        var operation = Assert.Single(client.PendingOperations());
        Assert.Equal(OperationType.Insert, operation.Type);
        Assert.Equal(id, operation.RecordId);
        Assert.Equal(0, operation.BaseRowVersion);
        Assert.Equal(1, operation.LogicalTime);
        Assert.Equal(new[] { "nickname", "coins" }, operation.Changes.Select(c => c.Field));
        Assert.Equal(10L, operation.Changes[1].NewValue);
    }

    [Fact]
    public void Update_JournalsOnlyChangedFieldsWithOldValues()
    {
        using var client = CreateClient(SchemaVersions.V1);
        var id = client.Insert(SchemaVersions.PlayerProfile, new Dictionary<string, object?> { ["nickname"] = "neo", ["coins"] = 10 });

        client.Update(SchemaVersions.PlayerProfile, id, new Dictionary<string, object?> { ["nickname"] = "neo", ["coins"] = 15 });

        var update = client.PendingOperations()[1];
        Assert.Equal(OperationType.Update, update.Type);
        var change = Assert.Single(update.Changes);
        Assert.Equal(("coins", (object?)10L, (object?)15L), (change.Field, change.OldValue, change.NewValue));
        Assert.Equal(2, update.LogicalTime);
    }

    [Fact]
    public void Update_WithoutChanges_IsNotJournaled()
    {
        using var client = CreateClient(SchemaVersions.V1);
        var id = client.Insert(SchemaVersions.PlayerProfile, new Dictionary<string, object?> { ["nickname"] = "neo" });

        client.Update(SchemaVersions.PlayerProfile, id, new Dictionary<string, object?> { ["nickname"] = "neo" });

        Assert.Equal(1, client.PendingOperationCount);
    }

    [Fact]
    public void Delete_MarksRowDeletedAndJournals()
    {
        using var client = CreateClient(SchemaVersions.V1);
        var id = client.Insert(SchemaVersions.PlayerProfile, new Dictionary<string, object?> { ["nickname"] = "neo" });

        client.Delete(SchemaVersions.PlayerProfile, id);

        Assert.True(client.Find(SchemaVersions.PlayerProfile, id)!.IsDeleted);
        Assert.Empty(client.GetAll(SchemaVersions.PlayerProfile));
        var delete = client.PendingOperations()[1];
        Assert.Equal(OperationType.Delete, delete.Type);
        Assert.Empty(delete.Changes);
    }

    [Fact]
    public void FieldsOutsideKnownSet_AreRejectedLocally()
    {
        using var client = CreateClient(SchemaVersions.V1);

        Assert.Throws<ArgumentException>(() =>
            client.Insert(SchemaVersions.PlayerProfile, new Dictionary<string, object?> { ["region"] = "eu" }));
        Assert.Throws<ArgumentException>(() =>
            client.Insert(SchemaVersions.ItemModification, new Dictionary<string, object?> { ["mod_type"] = "x" }));
        Assert.Equal(0, client.PendingOperationCount);
    }

    [Fact]
    public async Task SyncAsync_WhenOffline_DoesNotContactServerAndKeepsJournal()
    {
        using var client = CreateClient(SchemaVersions.V1);
        client.Insert(SchemaVersions.PlayerProfile, new Dictionary<string, object?> { ["nickname"] = "neo" });
        client.IsOnline = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SyncAsync());

        Assert.Empty(_transport.Requests);
        Assert.Equal(1, client.PendingOperationCount);
    }

    [Fact]
    public async Task SyncAsync_SendsJournalAndAppliesResponse()
    {
        using var client = CreateClient(SchemaVersions.V1);
        var id = client.Insert(SchemaVersions.PlayerProfile, new Dictionary<string, object?> { ["nickname"] = "neo", ["coins"] = 10 });
        var remoteId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

        _transport.Respond = request => new SyncResponse
        {
            ServerVersion = 4,
            Results = request.Operations.Select(o => new OperationResult { OpId = o.OpId, Outcome = OperationOutcome.Applied, RowVersion = 3 }).ToList(),
            Records =
            {
                new RecordState
                {
                    Table = SchemaVersions.PlayerProfile, RecordId = id, RowVersion = 3,
                    Values = { ["nickname"] = "neo", ["level"] = 2L, ["coins"] = 10L, ["settings_sound"] = true },
                },
            },
            RemoteChanges =
            {
                new RecordState
                {
                    Table = SchemaVersions.PlayerProfile, RecordId = remoteId, RowVersion = 4,
                    Values = { ["nickname"] = "trinity", ["level"] = 1L, ["coins"] = 0L, ["settings_sound"] = false },
                },
            },
        };

        await client.SyncAsync();

        var request = Assert.Single(_transport.Requests);
        Assert.Equal("client", request.ClientId);
        Assert.Equal(SchemaVersions.V1, request.SchemaVersion);
        Assert.Equal(0, request.SinceServerVersion);
        Assert.Single(request.Operations);

        Assert.Equal(0, client.PendingOperationCount);
        Assert.Equal(4, client.ServerVersion);
        var local = client.Find(SchemaVersions.PlayerProfile, id)!;
        Assert.Equal(3, local.RowVersion);
        Assert.Equal(2L, local.Values["level"]);
        var remote = client.Find(SchemaVersions.PlayerProfile, remoteId)!;
        Assert.Equal("trinity", remote.Values["nickname"]);
        Assert.Equal(false, remote.Values["settings_sound"]);
    }

    [Fact]
    public async Task SyncAsync_UsesLocalRowVersionAsBase()
    {
        using var client = CreateClient(SchemaVersions.V1);
        var id = client.Insert(SchemaVersions.PlayerProfile, new Dictionary<string, object?> { ["nickname"] = "neo" });
        _transport.Respond = request => new SyncResponse
        {
            ServerVersion = 7,
            Results = request.Operations.Select(o => new OperationResult { OpId = o.OpId, RowVersion = 7 }).ToList(),
            Records = { new RecordState { Table = SchemaVersions.PlayerProfile, RecordId = id, RowVersion = 7, Values = { ["nickname"] = "neo" } } },
        };
        await client.SyncAsync();

        client.Update(SchemaVersions.PlayerProfile, id, new Dictionary<string, object?> { ["coins"] = 5 });

        Assert.Equal(7, Assert.Single(client.PendingOperations()).BaseRowVersion);
        Assert.Equal(7, client.BuildRequest().SinceServerVersion);
    }

    [Fact]
    public async Task SyncAsync_WhenTransportFails_KeepsJournalForResend()
    {
        using var client = CreateClient(SchemaVersions.V1);
        client.Insert(SchemaVersions.PlayerProfile, new Dictionary<string, object?> { ["nickname"] = "neo" });
        _transport.Respond = _ => throw new HttpRequestException("connection lost");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SyncAsync());

        Assert.Equal(1, client.PendingOperationCount);
    }

    [Fact]
    public async Task SyncAsync_DeletedStateMarksLocalRowDeleted()
    {
        using var client = CreateClient(SchemaVersions.V1);
        var id = client.Insert(SchemaVersions.PlayerProfile, new Dictionary<string, object?> { ["nickname"] = "neo" });
        _transport.Respond = request => new SyncResponse
        {
            Results = request.Operations.Select(o => new OperationResult { OpId = o.OpId, Outcome = OperationOutcome.Rejected }).ToList(),
            Records = { new RecordState { Table = SchemaVersions.PlayerProfile, RecordId = id, IsDeleted = true } },
        };

        await client.SyncAsync();

        Assert.True(client.Find(SchemaVersions.PlayerProfile, id)!.IsDeleted);
        Assert.Equal(0, client.PendingOperationCount);
    }

    private SyncClient CreateClient(int version) =>
        new("client", version, _connection, _transport, _clock, DeterministicGuids.FromSeed(1));

    private sealed class FakeTransport : ISyncTransport
    {
        public List<SyncRequest> Requests { get; } = new();

        public Func<SyncRequest, SyncResponse> Respond { get; set; } = _ => new SyncResponse();

        public Task<SyncResponse> SendAsync(SyncRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }
}
