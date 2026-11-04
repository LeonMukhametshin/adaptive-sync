using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Strategies;

namespace Sync.Strategies.Tests;

public class AdaptiveStrategyTests
{
    private static readonly Guid RecordId = Guid.Parse("00000000-0000-4000-8000-000000000001");

    private readonly AdaptiveStrategy _strategy = new();

    [Fact]
    public void S1_LatestClientWithoutConflict_AppliesChange()
    {
        var decision = Decide(SchemaVersions.V3, Update(("coins", 100L, 120L)), Profile());

        Assert.Equal(OperationOutcome.Applied, decision.Outcome);
        Assert.Equal(Writes(("coins", 120L)), decision.FieldWrites);
        AssertField(decision, "coins", FieldOutcome.Applied, SyncSituation.S1);
        Assert.Equal(SyncSituation.S1, decision.Situation);
    }

    [Fact]
    public void S2_OldClientWithoutConflict_WritesOnlyKnownChangedFields()
    {
        var decision = Decide(SchemaVersions.V1, Update(("nickname", "neo", "trinity")), Profile());

        Assert.Equal(OperationOutcome.Applied, decision.Outcome);
        Assert.Equal(Writes(("nickname", "trinity")), decision.FieldWrites);
        AssertField(decision, "nickname", FieldOutcome.Applied, SyncSituation.S2);
        Assert.DoesNotContain(decision.Fields, f => f.Outcome == FieldOutcome.Overwritten);
    }

    [Fact]
    public void S3_ServerChangedFieldOutsideKnownSet_KeepsItAndAppliesClientChange()
    {
        var current = Profile();
        ChangeOnServer(current, "region", "us", rowVersion: 8, time: 15);

        var decision = Decide(SchemaVersions.V1, Update(("nickname", "neo", "trinity")), current);

        Assert.Equal(OperationOutcome.Applied, decision.Outcome);
        Assert.Equal(Writes(("nickname", "trinity")), decision.FieldWrites);
        AssertField(decision, "nickname", FieldOutcome.Applied, SyncSituation.S3);
        Assert.Equal(SyncSituation.S3, decision.Situation);
    }

    [Fact]
    public void S3_ChangeOutsideKnownSetSeenByClient_IsS2()
    {
        var current = Profile();
        ChangeOnServer(current, "region", "us", rowVersion: 5, time: 15);

        var decision = Decide(SchemaVersions.V1, Update(("nickname", "neo", "trinity")), current);

        AssertField(decision, "nickname", FieldOutcome.Applied, SyncSituation.S2);
    }

    [Fact]
    public void S4_Counter_AddsClientDeltaToServerValue()
    {
        var current = Profile();
        ChangeOnServer(current, "coins", 130L, rowVersion: 8, time: 15);

        var decision = Decide(SchemaVersions.V1, Update(("coins", 100L, 110L)), current);

        Assert.Equal(OperationOutcome.Merged, decision.Outcome);
        Assert.Equal(Writes(("coins", 140L)), decision.FieldWrites);
        AssertField(decision, "coins", FieldOutcome.Merged, SyncSituation.S4);
        Assert.Contains("coins", decision.Message);
    }

    [Theory]
    [InlineData("nickname", "morpheus", "trinity")]
    [InlineData("settings_sound", false, true)]
    public void S4_UserOwnedAndPreference_ClientWins(string field, object serverValue, object clientValue)
    {
        var current = Profile();
        var original = current.Values[field];
        ChangeOnServer(current, field, serverValue, rowVersion: 8, time: 25);

        var decision = Decide(SchemaVersions.V3, Update((field, original, clientValue)), current);

        Assert.Equal(OperationOutcome.Merged, decision.Outcome);
        Assert.Equal(Writes((field, clientValue)), decision.FieldWrites);
        AssertField(decision, field, FieldOutcome.Applied, SyncSituation.S4);
    }

    [Fact]
    public void S4_ServerAuthoritative_KeepsServerValue()
    {
        var current = Profile();
        ChangeOnServer(current, "level", 5L, rowVersion: 8, time: 15);

        var decision = Decide(SchemaVersions.V1, Update(("level", 1L, 2L)), current);

        Assert.Equal(OperationOutcome.Rejected, decision.Outcome);
        Assert.Empty(decision.FieldWrites);
        AssertField(decision, "level", FieldOutcome.Rejected, SyncSituation.S4);
    }

    [Theory]
    [InlineData(25L, "axe")]
    [InlineData(15L, "axe")]
    [InlineData(14L, "bow")]
    public void S4_Scalar_LastWriteWinsAndServerWinsTies(long serverTime, string expected)
    {
        var current = Item();
        ChangeOnServer(current, "item_type", "axe", rowVersion: 8, time: serverTime);

        var decision = Decide(SchemaVersions.V1, Update(SchemaVersions.InventoryItem, 15, ("item_type", "sword", "bow")), current);

        Assert.Equal(expected, decision.FieldWrites.TryGetValue("item_type", out var written) ? written : current.Values["item_type"]);
        AssertField(decision, "item_type", expected == "bow" ? FieldOutcome.Applied : FieldOutcome.Rejected, SyncSituation.S4);
    }

    [Fact]
    public void S4_MixedWithNonConflictingField_IsMergedPerField()
    {
        var current = Profile();
        ChangeOnServer(current, "level", 5L, rowVersion: 8, time: 15);

        var decision = Decide(SchemaVersions.V3, Update(("nickname", "neo", "trinity"), ("level", 1L, 2L)), current);

        Assert.Equal(OperationOutcome.Merged, decision.Outcome);
        Assert.Equal(Writes(("nickname", "trinity")), decision.FieldWrites);
        AssertField(decision, "nickname", FieldOutcome.Applied, SyncSituation.S1);
        AssertField(decision, "level", FieldOutcome.Rejected, SyncSituation.S4);
        Assert.Equal(SyncSituation.S4, decision.Situation);
    }

    [Fact]
    public void S5_OldClientDeletesRecordWithUnknownData_Tombstone()
    {
        var decision = Decide(SchemaVersions.V1, Delete(SchemaVersions.PlayerProfile), Profile());

        Assert.Equal(OperationOutcome.Tombstoned, decision.Outcome);
        Assert.True(decision.Tombstone);
        Assert.False(decision.HardDelete);
        Assert.Empty(decision.FieldWrites);
        Assert.Equal(SyncSituation.S5, decision.Situation);
        Assert.Contains("region", decision.Message);
    }

    [Fact]
    public void S5_OldClientDeletesRecordWithDependentsInNewerTable_Tombstone()
    {
        var current = Item(durability: 100L);
        current.HasDependents = true;
        current.DependentTables.Add(SchemaVersions.ItemModification);

        var decision = Decide(SchemaVersions.V2, Delete(SchemaVersions.InventoryItem), current);

        Assert.Equal(OperationOutcome.Tombstoned, decision.Outcome);
        Assert.True(decision.Tombstone);
        Assert.Contains(SchemaVersions.ItemModification, decision.Message);
    }

    [Fact]
    public void OldClientDeletesRecordWhoseUnknownFieldsHaveDefaults_PlainDelete()
    {
        var current = Profile(avatar: null, region: null, premium: 0L);
        current.HasDependents = true;
        current.DependentTables.Add(SchemaVersions.InventoryItem);

        var decision = Decide(SchemaVersions.V1, Delete(SchemaVersions.PlayerProfile), current);

        Assert.Equal(OperationOutcome.Applied, decision.Outcome);
        Assert.True(decision.HardDelete);
        Assert.False(decision.Tombstone);
        Assert.Equal(SyncSituation.S2, decision.Situation);
    }

    [Fact]
    public void LatestClientDelete_IsPlainDeleteEvenWithData()
    {
        var current = Item();
        current.DependentTables.Add(SchemaVersions.ItemModification);

        var decision = Decide(SchemaVersions.V3, Delete(SchemaVersions.InventoryItem), current);

        Assert.True(decision.HardDelete);
        Assert.Equal(SyncSituation.S1, decision.Situation);
    }

    [Fact]
    public void Insert_ByOldClient_AppliesSentFieldsOnly()
    {
        var operation = Update(SchemaVersions.InventoryItem, 20, ("player_id", null, RecordId), ("item_type", null, "shield"));
        operation.Type = OperationType.Insert;

        var decision = Decide(SchemaVersions.V1, operation, null);

        Assert.Equal(OperationOutcome.Applied, decision.Outcome);
        Assert.Equal(new[] { "player_id", "item_type" }, decision.FieldWrites.Keys);
        Assert.All(decision.Fields, f => Assert.Equal(SyncSituation.S2, f.Situation));
    }

    [Fact]
    public void ChangeOfDeletedRecord_IsRejected()
    {
        var current = Profile();
        current.IsDeleted = true;

        Assert.Equal(OperationOutcome.Rejected, Decide(SchemaVersions.V3, Update(("coins", 100L, 1L)), current).Outcome);
        Assert.Equal(OperationOutcome.Rejected, Decide(SchemaVersions.V3, Update(("coins", 100L, 1L)), null).Outcome);
    }

    [Fact]
    public void FieldOutsideKnownSet_IsNeverWritten()
    {
        var decision = Decide(SchemaVersions.V1, Update(("nickname", "neo", "trinity"), ("region", "eu", "us")), Profile());

        Assert.Equal(Writes(("nickname", "trinity")), decision.FieldWrites);
        AssertField(decision, "region", FieldOutcome.Rejected, null);
    }

    private StrategyDecision Decide(int version, SyncOperation operation, ServerRecord? current) =>
        _strategy.Decide(new SyncContext(SchemaVersions.Registry, "client", version), operation, current);

    private static void AssertField(StrategyDecision decision, string field, FieldOutcome outcome, SyncSituation? situation)
    {
        var actual = Assert.Single(decision.Fields, f => f.Field == field);
        Assert.Equal(outcome, actual.Outcome);
        Assert.Equal(situation, actual.Situation);
    }

    private static ServerRecord Profile(string? avatar = "a0", string? region = "eu", long premium = 7L) =>
        Record(SchemaVersions.PlayerProfile, new Dictionary<string, object?>
        {
            ["nickname"] = "neo", ["level"] = 1L, ["coins"] = 100L, ["settings_sound"] = true,
            ["avatar_id"] = avatar, ["premium_currency"] = premium, ["region"] = region,
        });

    private static ServerRecord Item(long durability = 90L) =>
        Record(SchemaVersions.InventoryItem, new Dictionary<string, object?>
        {
            ["player_id"] = RecordId, ["item_type"] = "sword", ["quantity"] = 1L, ["durability"] = durability,
        });

    private static ServerRecord Record(string table, Dictionary<string, object?> values)
    {
        var record = new ServerRecord { Table = table, RecordId = RecordId, RowVersion = 5, Values = values };
        foreach (var field in values.Keys)
            record.FieldVersions[field] = new FieldVersion(5, 10);
        return record;
    }

    private static void ChangeOnServer(ServerRecord record, string field, object? value, long rowVersion, long time)
    {
        record.Values[field] = value;
        record.RowVersion = Math.Max(record.RowVersion, rowVersion);
        record.FieldVersions[field] = new FieldVersion(rowVersion, time);
    }

    private static SyncOperation Update(params (string Field, object? Old, object? New)[] changes) =>
        Update(SchemaVersions.PlayerProfile, 20, changes);

    private static SyncOperation Update(string table, long time, params (string Field, object? Old, object? New)[] changes) =>
        new()
        {
            OpId = Guid.Parse("00000000-0000-4000-8000-0000000000aa"),
            Table = table,
            RecordId = RecordId,
            Type = OperationType.Update,
            BaseRowVersion = 5,
            LogicalTime = time,
            Changes = changes.Select(c => new FieldChange { Field = c.Field, OldValue = c.Old, NewValue = c.New }).ToList(),
        };

    private static SyncOperation Delete(string table)
    {
        var operation = Update(table, 20);
        operation.Type = OperationType.Delete;
        return operation;
    }

    private static Dictionary<string, object?> Writes(params (string Field, object? Value)[] values) =>
        values.ToDictionary(v => v.Field, v => v.Value);
}
