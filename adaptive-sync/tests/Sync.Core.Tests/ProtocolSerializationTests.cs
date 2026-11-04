using System.Text.Json;
using Sync.Core.Protocol;

namespace Sync.Core.Tests;

public class ProtocolSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SyncRequest_RoundTripsThroughJson()
    {
        var request = new SyncRequest
        {
            ClientId = "client-1",
            AppVersion = "1.0.0",
            SchemaVersion = 1,
            SinceServerVersion = 7,
            Operations =
            {
                new SyncOperation
                {
                    OpId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    Table = "player_profile",
                    RecordId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
                    Type = OperationType.Update,
                    BaseRowVersion = 3,
                    LogicalTime = 42,
                    Changes = { new FieldChange { Field = "coins", OldValue = 10L, NewValue = 15L } },
                },
            },
        };

        var json = JsonSerializer.Serialize(request, Options);
        var copy = JsonSerializer.Deserialize<SyncRequest>(json, Options)!;

        Assert.Equal("client-1", copy.ClientId);
        Assert.Equal(1, copy.SchemaVersion);
        Assert.Equal(7, copy.SinceServerVersion);
        var op = Assert.Single(copy.Operations);
        Assert.Equal(request.Operations[0].OpId, op.OpId);
        Assert.Equal(OperationType.Update, op.Type);
        Assert.Equal(3, op.BaseRowVersion);
        Assert.Equal(42, op.LogicalTime);
        var change = Assert.Single(op.Changes);
        Assert.Equal("coins", change.Field);
        Assert.Equal(15L, ((JsonElement)change.NewValue!).GetInt64());
    }

    [Fact]
    public void SyncResponse_RoundTripsThroughJson()
    {
        var response = new SyncResponse
        {
            ServerVersion = 12,
            Results = { new OperationResult { OpId = Guid.NewGuid(), Outcome = OperationOutcome.Tombstoned, RowVersion = 5 } },
            Records = { new RecordState { Table = "player_profile", RecordId = Guid.NewGuid(), RowVersion = 5, IsDeleted = true } },
        };

        var copy = JsonSerializer.Deserialize<SyncResponse>(JsonSerializer.Serialize(response, Options), Options)!;

        Assert.Equal(12, copy.ServerVersion);
        Assert.Equal(OperationOutcome.Tombstoned, Assert.Single(copy.Results).Outcome);
        Assert.True(Assert.Single(copy.Records).IsDeleted);
        Assert.Empty(copy.RemoteChanges);
    }
}
