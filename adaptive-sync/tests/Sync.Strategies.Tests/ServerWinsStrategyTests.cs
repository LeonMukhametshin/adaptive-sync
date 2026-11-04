using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Strategies;

namespace Sync.Strategies.Tests;

public class ServerWinsStrategyTests
{
    private static readonly Guid RecordId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly ServerWinsStrategy _strategy = new();

    [Fact]
    public void Insert_OfNewRecord_IsApplied()
    {
        var operation = Operation(OperationType.Insert, 0, ("nickname", null, "neo"), ("coins", null, 10L));

        var decision = _strategy.Decide(Context(SchemaVersions.V3), operation, current: null);

        Assert.Equal(OperationOutcome.Applied, decision.Outcome);
        Assert.Equal(new Dictionary<string, object?> { ["nickname"] = "neo", ["coins"] = 10L }, decision.FieldWrites);
        Assert.All(decision.Fields, f => Assert.Equal(FieldOutcome.Applied, f.Outcome));
    }

    [Fact]
    public void Insert_OfExistingRecord_IsRejected()
    {
        var decision = _strategy.Decide(Context(SchemaVersions.V3), Operation(OperationType.Insert, 0, ("nickname", null, "neo")), Record(5));

        Assert.Equal(OperationOutcome.Rejected, decision.Outcome);
        Assert.Empty(decision.FieldWrites);
    }

    [Fact]
    public void S1_UpdateWithoutConflict_IsApplied()
    {
        var decision = _strategy.Decide(
            Context(SchemaVersions.V3), Operation(OperationType.Update, 5, ("coins", 10L, 15L)), Record(5));

        Assert.Equal(OperationOutcome.Applied, decision.Outcome);
        Assert.Equal(15L, decision.FieldWrites["coins"]);
    }

    [Fact]
    public void S2_OldClientUpdate_WritesOnlyFieldsItSent()
    {
        var current = Record(5);
        current.Values["region"] = "eu";

        var decision = _strategy.Decide(
            Context(SchemaVersions.V1), Operation(OperationType.Update, 5, ("nickname", "neo", "trinity")), current);

        Assert.Equal(OperationOutcome.Applied, decision.Outcome);
        Assert.Equal(new[] { "nickname" }, decision.FieldWrites.Keys);
    }

    [Fact]
    public void S3_ServerChangedFieldOutsideKnownSet_RejectsWholeClientChange()
    {
        var current = Record(8);
        current.FieldVersions["region"] = new FieldVersion(8, 30);

        var decision = _strategy.Decide(
            Context(SchemaVersions.V1), Operation(OperationType.Update, 5, ("nickname", "neo", "trinity")), current);

        Assert.Equal(OperationOutcome.Rejected, decision.Outcome);
        Assert.Empty(decision.FieldWrites);
        Assert.Equal(FieldOutcome.Rejected, Assert.Single(decision.Fields).Outcome);
        Assert.Contains("row_version 8", decision.Message);
    }

    [Fact]
    public void S4_ConcurrentChangeOfSameField_KeepsServerValue()
    {
        var current = Record(9);
        current.FieldVersions["coins"] = new FieldVersion(9, 40);

        var decision = _strategy.Decide(
            Context(SchemaVersions.V3), Operation(OperationType.Update, 5, ("coins", 10L, 15L)), current);

        Assert.Equal(OperationOutcome.Rejected, decision.Outcome);
        Assert.Empty(decision.FieldWrites);
    }

    [Fact]
    public void Update_OfMissingOrDeletedRecord_IsRejected()
    {
        var deleted = Record(5);
        deleted.IsDeleted = true;
        var operation = Operation(OperationType.Update, 5, ("coins", 10L, 15L));

        Assert.Equal(OperationOutcome.Rejected, _strategy.Decide(Context(SchemaVersions.V3), operation, null).Outcome);
        Assert.Equal(OperationOutcome.Rejected, _strategy.Decide(Context(SchemaVersions.V3), operation, deleted).Outcome);
    }

    [Fact]
    public void Delete_WithoutConflict_DeletesRecord()
    {
        var decision = _strategy.Decide(Context(SchemaVersions.V3), Operation(OperationType.Delete, 5), Record(5));

        Assert.Equal(OperationOutcome.Applied, decision.Outcome);
        Assert.True(decision.HardDelete);
        Assert.False(decision.Tombstone);
    }

    [Fact]
    public void S5_StaleDeleteByOldClient_IsRejected()
    {
        var current = Record(7);
        current.HasDependents = true;

        var decision = _strategy.Decide(Context(SchemaVersions.V1), Operation(OperationType.Delete, 5), current);

        Assert.Equal(OperationOutcome.Rejected, decision.Outcome);
        Assert.False(decision.HardDelete);
    }

    [Fact]
    public void Registry_CreatesServerWinsByName()
    {
        Assert.IsType<ServerWinsStrategy>(SyncStrategies.Create("serverwins"));
        Assert.Throws<ArgumentException>(() => SyncStrategies.Create("unknown"));
    }

    private static SyncContext Context(int version) => new(SchemaVersions.Registry, "client", version);

    private static ServerRecord Record(long rowVersion) => new()
    {
        Table = SchemaVersions.PlayerProfile,
        RecordId = RecordId,
        RowVersion = rowVersion,
        Values = { ["nickname"] = "neo", ["coins"] = 10L },
    };

    private static SyncOperation Operation(OperationType type, long baseRowVersion, params (string Field, object? Old, object? New)[] changes) =>
        new()
        {
            OpId = Guid.NewGuid(),
            Table = SchemaVersions.PlayerProfile,
            RecordId = RecordId,
            Type = type,
            BaseRowVersion = baseRowVersion,
            LogicalTime = 1,
            Changes = changes.Select(c => new FieldChange { Field = c.Field, OldValue = c.Old, NewValue = c.New }).ToList(),
        };
}
