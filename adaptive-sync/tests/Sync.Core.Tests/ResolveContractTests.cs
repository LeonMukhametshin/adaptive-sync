using System.Text.Json;
using Sync.Core.Protocol;
using Sync.Core.Resolver;
using Sync.Core.Schema;
using Sync.Core.Strategies;

namespace Sync.Core.Tests;

public class ResolveContractTests
{
    private static readonly Guid Id = Guid.Parse("6f9619ff-8b86-4d11-b42d-00c04fc964ff");

    [Fact]
    public void Request_WithEveryFieldValueType_RoundTripsAndNormalizes()
    {
        var request = new ResolveRequest
        {
            ClientId = "c1",
            SchemaVersion = SchemaVersions.V1,
            Items =
            {
                new ResolveItem
                {
                    Operation = new SyncOperation
                    {
                        OpId = Id,
                        Table = SchemaVersions.PlayerProfile,
                        RecordId = Id,
                        Type = OperationType.Update,
                        BaseRowVersion = 5,
                        LogicalTime = 20,
                        Changes =
                        {
                            new FieldChange { Field = "nickname", OldValue = "neo", NewValue = "trinity" },
                            new FieldChange { Field = "coins", OldValue = 100L, NewValue = 110L },
                            new FieldChange { Field = "settings_sound", OldValue = true, NewValue = false },
                        },
                    },
                    Current = new ServerRecord
                    {
                        Table = SchemaVersions.PlayerProfile,
                        RecordId = Id,
                        RowVersion = 8,
                        Values = { ["nickname"] = "neo", ["coins"] = 130L, ["settings_sound"] = true, ["region"] = null },
                        FieldVersions = { ["coins"] = new FieldVersion(8, 15) },
                        HasDependents = true,
                        DependentTables = { SchemaVersions.InventoryItem },
                    },
                },
                new ResolveItem
                {
                    Operation = new SyncOperation
                    {
                        Table = SchemaVersions.InventoryItem,
                        RecordId = Id,
                        Type = OperationType.Insert,
                        Changes = { new FieldChange { Field = "player_id", NewValue = Id } },
                    },
                },
            },
        };

        var copy = JsonSerializer.Deserialize<ResolveRequest>(JsonSerializer.Serialize(request, SyncJson.Options), SyncJson.Options)!;
        foreach (var item in copy.Items)
            ResolveValues.Normalize(SchemaVersions.Registry, item);

        Assert.Equal(ResolveContract.Version, copy.ContractVersion);
        var first = copy.Items[0];
        Assert.Equal(new object?[] { "trinity", 110L, false }, first.Operation.Changes.Select(c => c.NewValue));
        Assert.Equal(100L, first.Operation.Changes[1].OldValue);
        Assert.Equal(130L, first.Current!.Values["coins"]);
        Assert.Equal(true, first.Current.Values["settings_sound"]);
        Assert.Null(first.Current.Values["region"]);
        Assert.Equal(8, first.Current.FieldVersions["coins"].RowVersion);
        Assert.Equal(15, first.Current.FieldVersions["coins"].LogicalTime);
        Assert.Equal(new[] { SchemaVersions.InventoryItem }, first.Current.DependentTables);
        Assert.Equal(Id, copy.Items[1].Operation.Changes[0].NewValue);
        Assert.Null(copy.Items[1].Current);
    }

    [Fact]
    public void Response_WithTombstoneAndSituations_RoundTrips()
    {
        var response = new ResolveResponse
        {
            Strategy = "Adaptive",
            Decisions =
            {
                new StrategyDecision
                {
                    Outcome = OperationOutcome.Merged,
                    FieldWrites = { ["coins"] = 140L, ["nickname"] = "trinity" },
                    Fields =
                    {
                        new FieldDecision("coins", FieldOutcome.Merged, SyncSituation.S4),
                        new FieldDecision("nickname", FieldOutcome.Applied, SyncSituation.S3),
                        new FieldDecision("region", FieldOutcome.Rejected),
                    },
                    Situation = SyncSituation.S4,
                    Message = "merged",
                },
                new StrategyDecision { Outcome = OperationOutcome.Tombstoned, Tombstone = true, Situation = SyncSituation.S5 },
            },
        };

        var json = JsonSerializer.Serialize(response, SyncJson.Options);
        var copy = JsonSerializer.Deserialize<ResolveResponse>(json, SyncJson.Options)!;
        var merged = copy.Decisions[0];
        ResolveValues.Normalize(SchemaVersions.Registry, SchemaVersions.PlayerProfile, merged);

        Assert.Contains("\"outcome\":\"Merged\"", json);
        Assert.Equal("Adaptive", copy.Strategy);
        Assert.Equal(new Dictionary<string, object?> { ["coins"] = 140L, ["nickname"] = "trinity" }, merged.FieldWrites);
        Assert.Equal(
            new (string, FieldOutcome, SyncSituation?)[]
            {
                ("coins", FieldOutcome.Merged, SyncSituation.S4),
                ("nickname", FieldOutcome.Applied, SyncSituation.S3),
                ("region", FieldOutcome.Rejected, null),
            },
            merged.Fields.Select(f => (f.Field, f.Outcome, f.Situation)));
        Assert.Equal(SyncSituation.S4, merged.Situation);
        Assert.True(copy.Decisions[1].Tombstone);
        Assert.Equal(OperationOutcome.Tombstoned, copy.Decisions[1].Outcome);
        Assert.Equal(SyncSituation.S5, copy.Decisions[1].Situation);
    }

    [Fact]
    public void Normalize_UnknownField_Throws()
    {
        var item = new ResolveItem
        {
            Operation = new SyncOperation
            {
                Table = SchemaVersions.PlayerProfile,
                Changes = { new FieldChange { Field = "missing", NewValue = 1L } },
            },
        };

        Assert.Throws<FormatException>(() => ResolveValues.Normalize(SchemaVersions.Registry, item));
    }
}
