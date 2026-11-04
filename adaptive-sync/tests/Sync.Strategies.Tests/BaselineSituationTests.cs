using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Strategies;

namespace Sync.Strategies.Tests;

public class BaselineSituationTests
{
    private const string ServerWins = ServerWinsStrategy.StrategyName;
    private const string Lww = LwwStrategy.StrategyName;
    private const string ClientWins = ClientWinsStrategy.StrategyName;
    private const string FieldLww = FieldLwwNoSchemaStrategy.StrategyName;

    private static readonly Guid RecordId = Guid.Parse("00000000-0000-4000-8000-000000000001");

    private static readonly Dictionary<string, object?> V1Defaults = new()
    {
        ["avatar_id"] = null,
        ["premium_currency"] = 0L,
        ["region"] = null,
    };

    [Theory]
    [InlineData(ServerWins)]
    [InlineData(Lww)]
    [InlineData(ClientWins)]
    [InlineData(FieldLww)]
    public void S1_SameVersionWithoutConflict_AllApplyClientChange(string strategy)
    {
        var decision = Decide(strategy, SchemaVersions.V3, Update(("coins", 100L, 120L)), Profile());

        Assert.Equal(OperationOutcome.Applied, decision.Outcome);
        Assert.Equal(Writes(("coins", 120L)), decision.FieldWrites);
        Assert.DoesNotContain(decision.Fields, f => f.Outcome == FieldOutcome.Overwritten);
    }

    [Theory]
    [InlineData(ServerWins, false)]
    [InlineData(FieldLww, false)]
    [InlineData(Lww, true)]
    [InlineData(ClientWins, true)]
    public void S2_OldClientWithoutConflict_RecordLevelOverwriteLosesUnknownFields(string strategy, bool losesUnknownFields)
    {
        var decision = Decide(strategy, SchemaVersions.V1, Update(("nickname", "neo", "trinity")), Profile());

        Assert.Equal(OperationOutcome.Applied, decision.Outcome);
        Assert.Equal(Expected(("nickname", "trinity"), losesUnknownFields), decision.FieldWrites);
        Assert.Equal(losesUnknownFields ? 3 : 0, decision.Fields.Count(f => f.Outcome == FieldOutcome.Overwritten));
    }

    [Theory]
    [InlineData(ServerWins, OperationOutcome.Rejected, false)]
    [InlineData(FieldLww, OperationOutcome.Applied, false)]
    [InlineData(Lww, OperationOutcome.Applied, true)]
    [InlineData(ClientWins, OperationOutcome.Applied, true)]
    public void S3_ServerChangedFieldOutsideKnownSet(string strategy, OperationOutcome outcome, bool losesUnknownFields)
    {
        var current = Profile();
        ChangeOnServer(current, "region", "us", rowVersion: 8, time: 15);

        var decision = Decide(strategy, SchemaVersions.V1, Update(("nickname", "neo", "trinity")), current);

        Assert.Equal(outcome, decision.Outcome);
        if (outcome == OperationOutcome.Rejected)
            Assert.Empty(decision.FieldWrites);
        else
            Assert.Equal(Expected(("nickname", "trinity"), losesUnknownFields), decision.FieldWrites);
    }

    [Theory]
    [InlineData(ServerWins, OperationOutcome.Rejected, 130L)]
    [InlineData(Lww, OperationOutcome.Applied, 110L)]
    [InlineData(ClientWins, OperationOutcome.Applied, 110L)]
    [InlineData(FieldLww, OperationOutcome.Applied, 110L)]
    public void S4_CounterChangedOnBothSides_NoBaselineSumsIncrements(string strategy, OperationOutcome outcome, long finalCoins)
    {
        var current = Profile();
        ChangeOnServer(current, "coins", 130L, rowVersion: 8, time: 15);

        var decision = Decide(strategy, SchemaVersions.V3, Update(("coins", 100L, 110L)), current);

        Assert.Equal(outcome, decision.Outcome);
        Assert.Equal(finalCoins, ResultingValue(current, decision, "coins"));
        Assert.NotEqual(140L, finalCoins);
    }

    [Theory]
    [InlineData(ServerWins, OperationOutcome.Rejected)]
    [InlineData(Lww, OperationOutcome.Rejected)]
    [InlineData(FieldLww, OperationOutcome.Rejected)]
    [InlineData(ClientWins, OperationOutcome.Applied)]
    public void S4_ServerChangeIsNewer_OnlyClientWinsOverwritesIt(string strategy, OperationOutcome outcome)
    {
        var current = Profile();
        ChangeOnServer(current, "nickname", "morpheus", rowVersion: 8, time: 15);

        var decision = Decide(strategy, SchemaVersions.V3, Update(("nickname", "neo", "trinity"), time: 12), current);

        Assert.Equal(outcome, decision.Outcome);
        Assert.Equal(outcome == OperationOutcome.Applied ? "trinity" : "morpheus", ResultingValue(current, decision, "nickname"));
    }

    [Theory]
    [InlineData(ServerWins)]
    [InlineData(Lww)]
    [InlineData(ClientWins)]
    [InlineData(FieldLww)]
    public void S5_OldClientDeletesRecordWithUnknownData_AllDeleteWithoutTombstone(string strategy)
    {
        var current = Profile();
        current.HasDependents = true;

        var decision = Decide(strategy, SchemaVersions.V1, Delete(), current);

        Assert.Equal(OperationOutcome.Applied, decision.Outcome);
        Assert.True(decision.HardDelete);
        Assert.False(decision.Tombstone);
    }

    [Fact]
    public void FieldLww_MixedFieldTimes_MergesPerField()
    {
        var current = Profile();
        ChangeOnServer(current, "coins", 130L, rowVersion: 8, time: 25);

        var decision = Decide(FieldLww, SchemaVersions.V3, Update(("nickname", "neo", "trinity"), ("coins", 100L, 110L)), current);

        Assert.Equal(OperationOutcome.Merged, decision.Outcome);
        Assert.Equal(Writes(("nickname", "trinity")), decision.FieldWrites);
        Assert.Equal(FieldOutcome.Rejected, decision.Fields.Single(f => f.Field == "coins").Outcome);
        Assert.Contains("coins", decision.Message);
    }

    [Fact]
    public void Lww_EqualTimes_KeepsServerValue()
    {
        var current = Profile();
        ChangeOnServer(current, "nickname", "morpheus", rowVersion: 8, time: 20);

        var decision = Decide(Lww, SchemaVersions.V3, Update(("nickname", "neo", "trinity")), current);

        Assert.Equal(OperationOutcome.Rejected, decision.Outcome);
    }

    [Theory]
    [InlineData(Lww)]
    [InlineData(ClientWins)]
    public void RecordLevelStrategies_InsertOverExistingRecord_OverwritesIt(string strategy)
    {
        var operation = Update(("nickname", null, "trinity"));
        operation.Type = OperationType.Insert;

        var decision = Decide(strategy, SchemaVersions.V1, operation, Profile());

        Assert.Equal(OperationOutcome.Applied, decision.Outcome);
        Assert.Equal(Expected(("nickname", "trinity"), losesUnknownFields: true), decision.FieldWrites);
    }

    [Theory]
    [InlineData(ServerWins)]
    [InlineData(Lww)]
    [InlineData(ClientWins)]
    [InlineData(FieldLww)]
    public void UpdateOfDeletedRecord_IsRejected(string strategy)
    {
        var current = Profile();
        current.IsDeleted = true;

        Assert.Equal(OperationOutcome.Rejected, Decide(strategy, SchemaVersions.V3, Update(("coins", 100L, 1L)), current).Outcome);
        Assert.Equal(OperationOutcome.Rejected, Decide(strategy, SchemaVersions.V3, Update(("coins", 100L, 1L)), null).Outcome);
    }

    [Theory]
    [InlineData("ServerWins", typeof(ServerWinsStrategy))]
    [InlineData("lww", typeof(LwwStrategy))]
    [InlineData("ClientWins", typeof(ClientWinsStrategy))]
    [InlineData("FieldLwwNoSchema", typeof(FieldLwwNoSchemaStrategy))]
    [InlineData("adaptive", typeof(AdaptiveStrategy))]
    public void Registry_CreatesEachStrategy(string name, Type expected)
    {
        Assert.IsType(expected, SyncStrategies.Create(name));
        Assert.Equal(5, SyncStrategies.Names.Count);
    }

    private static StrategyDecision Decide(string strategy, int version, SyncOperation operation, ServerRecord? current) =>
        SyncStrategies.Create(strategy).Decide(new SyncContext(SchemaVersions.Registry, "client", version), operation, current);

    private static ServerRecord Profile()
    {
        var record = new ServerRecord
        {
            Table = SchemaVersions.PlayerProfile,
            RecordId = RecordId,
            RowVersion = 5,
            Values =
            {
                ["nickname"] = "neo", ["level"] = 1L, ["coins"] = 100L, ["settings_sound"] = true,
                ["avatar_id"] = "a0", ["premium_currency"] = 7L, ["region"] = "eu",
            },
        };
        foreach (var field in record.Values.Keys)
            record.FieldVersions[field] = new FieldVersion(5, 10);
        return record;
    }

    private static void ChangeOnServer(ServerRecord record, string field, object? value, long rowVersion, long time)
    {
        record.Values[field] = value;
        record.RowVersion = rowVersion;
        record.FieldVersions[field] = new FieldVersion(rowVersion, time);
    }

    private static SyncOperation Update(params (string Field, object? Old, object? New)[] changes) => Update(changes, time: 20);

    private static SyncOperation Update((string Field, object? Old, object? New) change, long time) => Update(new[] { change }, time);

    private static SyncOperation Update((string Field, object? Old, object? New)[] changes, long time) =>
        new()
        {
            OpId = Guid.Parse("00000000-0000-4000-8000-0000000000aa"),
            Table = SchemaVersions.PlayerProfile,
            RecordId = RecordId,
            Type = OperationType.Update,
            BaseRowVersion = 5,
            LogicalTime = time,
            Changes = changes.Select(c => new FieldChange { Field = c.Field, OldValue = c.Old, NewValue = c.New }).ToList(),
        };

    private static SyncOperation Delete()
    {
        var operation = Update(Array.Empty<(string, object?, object?)>(), time: 20);
        operation.Type = OperationType.Delete;
        return operation;
    }

    private static object? ResultingValue(ServerRecord current, StrategyDecision decision, string field) =>
        decision.FieldWrites.TryGetValue(field, out var written) ? written : current.Values[field];

    private static Dictionary<string, object?> Writes(params (string Field, object? Value)[] values) =>
        values.ToDictionary(v => v.Field, v => v.Value);

    private static Dictionary<string, object?> Expected((string Field, object? Value) change, bool losesUnknownFields)
    {
        var expected = Writes(change);
        if (losesUnknownFields)
        {
            foreach (var (field, value) in V1Defaults)
                expected[field] = value;
        }
        return expected;
    }
}
