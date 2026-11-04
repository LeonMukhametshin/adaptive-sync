using System.Text.Json;
using Sync.Core.Protocol;
using Sync.Core.Schema;

namespace Sync.Core.Tests;

public class SyncJsonContextTests
{
    [Fact]
    public void Options_UseOnlySourceGeneratedMetadata()
    {
        Assert.Same(SyncJsonContext.Default, SyncJson.Options.TypeInfoResolver);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new UnknownDto(), SyncJson.Options));
    }

    [Fact]
    public void Request_WithEveryFieldValueType_RoundTripsWithoutReflection()
    {
        var id = Guid.Parse("6f9619ff-8b86-4d11-b42d-00c04fc964ff");
        var request = new SyncRequest
        {
            ClientId = "c1",
            SchemaVersion = 1,
            Operations =
            {
                new SyncOperation
                {
                    OpId = id,
                    Table = SchemaVersions.InventoryItem,
                    RecordId = id,
                    Type = OperationType.Insert,
                    Changes =
                    {
                        new FieldChange { Field = "player_id", NewValue = id },
                        new FieldChange { Field = "item_type", NewValue = "sword" },
                        new FieldChange { Field = "quantity", OldValue = 1L, NewValue = 2L },
                        new FieldChange { Field = "flag", NewValue = true },
                        new FieldChange { Field = "count", NewValue = 3 },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(request, SyncJson.Options);
        var copy = JsonSerializer.Deserialize<SyncRequest>(json, SyncJson.Options)!;

        Assert.Contains("\"type\":\"Insert\"", json);
        var changes = copy.Operations[0].Changes;
        Assert.Equal(id, FieldValues.Normalize(FieldType.Uuid, changes[0].NewValue));
        Assert.Equal("sword", FieldValues.Normalize(FieldType.Text, changes[1].NewValue));
        Assert.Equal(2L, FieldValues.Normalize(FieldType.Integer, changes[2].NewValue));
        Assert.Equal(true, FieldValues.Normalize(FieldType.Boolean, changes[3].NewValue));
        Assert.Equal(3L, FieldValues.Normalize(FieldType.Integer, changes[4].NewValue));
    }

    [Fact]
    public void Response_WithRecordValues_RoundTripsWithoutReflection()
    {
        var response = new SyncResponse
        {
            ServerVersion = 4,
            Results = { new OperationResult { OpId = Guid.Empty, Outcome = OperationOutcome.Merged, RowVersion = 4 } },
            Records = { new RecordState { Table = "player_profile", Values = { ["nickname"] = "neo", ["coins"] = 5L, ["region"] = null } } },
        };

        var copy = JsonSerializer.Deserialize<SyncResponse>(JsonSerializer.Serialize(response, SyncJson.Options), SyncJson.Options)!;

        Assert.Equal(OperationOutcome.Merged, copy.Results[0].Outcome);
        Assert.Equal(5L, FieldValues.Normalize(FieldType.Integer, copy.Records[0].Values["coins"]));
        Assert.Null(FieldValues.Normalize(FieldType.Text, copy.Records[0].Values["region"]));
    }

    [Fact]
    public void JournalChanges_RoundTripWithoutReflection()
    {
        var changes = new List<FieldChange> { new() { Field = "coins", OldValue = 1L, NewValue = 2L } };

        var copy = JsonSerializer.Deserialize<List<FieldChange>>(JsonSerializer.Serialize(changes, SyncJson.Options), SyncJson.Options)!;

        Assert.Equal(2L, FieldValues.Normalize(FieldType.Integer, Assert.Single(copy).NewValue));
    }

    private sealed class UnknownDto
    {
        public int Value { get; set; }
    }
}
