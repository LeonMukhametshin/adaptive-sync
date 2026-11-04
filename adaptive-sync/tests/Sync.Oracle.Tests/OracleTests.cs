using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Strategies;

namespace Sync.Oracle.Tests;

public class OracleTests
{
    private const string Profile = SchemaVersions.PlayerProfile;
    private const string Item = SchemaVersions.InventoryItem;
    private const string Modification = SchemaVersions.ItemModification;

    private static readonly Guid P = Guid.Parse("00000000-0000-4000-8000-000000000001");
    private static readonly Guid I = Guid.Parse("00000000-0000-4000-8000-000000000002");
    private static readonly Guid M = Guid.Parse("00000000-0000-4000-8000-000000000003");

    private readonly SyncOracle _oracle = new(SchemaVersions.Registry);

    [Fact]
    public void S1_LatestClientWithoutConflict_ChangeApplied()
    {
        var result = Run(new[] { ProfileRecord() },
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("coins", 100L, 120L)));

        Assert.Equal(120L, result.Value(Profile, P, "coins"));
        AssertClassified(result, (0, "coins", SyncSituation.S1));
    }

    [Fact]
    public void S2_OldClientWithoutConflict_ChangeAppliedUnknownFieldsUntouched()
    {
        var result = Run(new[] { ProfileRecord() },
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("nickname", "neo", "trinity")));

        Assert.Equal("trinity", result.Value(Profile, P, "nickname"));
        Assert.Equal("eu", result.Value(Profile, P, "region"));
        Assert.Equal("a0", result.Value(Profile, P, "avatar_id"));
        AssertClassified(result, (0, "nickname", SyncSituation.S2));
    }

    [Fact]
    public void S3_UnknownFieldChangedOnServerAfterClientSaw_BothKept()
    {
        var result = Run(new[] { ProfileRecord() },
            Server(Profile, P, at: 15, ("region", "eu", "us")),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("nickname", "neo", "trinity")));

        Assert.Equal("us", result.Value(Profile, P, "region"));
        Assert.Equal("trinity", result.Value(Profile, P, "nickname"));
        AssertClassified(result, (1, "nickname", SyncSituation.S3));
    }

    [Fact]
    public void S3_NotTriggeredByChangesTheClientAlreadySaw()
    {
        var result = Run(new[] { ProfileRecord() },
            Server(Profile, P, at: 5, ("region", "eu", "us")),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("nickname", "neo", "trinity")));

        AssertClassified(result, (1, "nickname", SyncSituation.S2));
    }

    [Fact]
    public void S4_Counter_SumsIncrementsOfBothSides()
    {
        var result = Run(new[] { ProfileRecord() },
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 14, seen: 10, delivered: 15, ("coins", 100L, 130L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("coins", 100L, 110L)));

        Assert.Equal(140L, result.Value(Profile, P, "coins"));
        AssertClassified(result, (0, "coins", SyncSituation.S1), (1, "coins", SyncSituation.S4));
    }

    [Fact]
    public void S4_UserOwned_IncomingClientWins()
    {
        var result = Run(new[] { ProfileRecord() },
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 14, seen: 10, delivered: 15, ("nickname", "neo", "morpheus")),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("nickname", "neo", "trinity")));

        Assert.Equal("trinity", result.Value(Profile, P, "nickname"));
        AssertClassified(result, (0, "nickname", SyncSituation.S1), (1, "nickname", SyncSituation.S4));
    }

    [Fact]
    public void S4_UserPreference_IncomingClientWins()
    {
        var result = Run(new[] { ProfileRecord() },
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 14, seen: 10, delivered: 15, ("settings_sound", true, false)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("settings_sound", true, true)));

        Assert.Equal(true, result.Value(Profile, P, "settings_sound"));
    }

    [Fact]
    public void S4_ServerAuthoritative_StoredServerValueWins()
    {
        var result = Run(new[] { ProfileRecord() },
            Server(Profile, P, at: 15, ("level", 1L, 5L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("level", 1L, 2L)));

        Assert.Equal(5L, result.Value(Profile, P, "level"));
        AssertClassified(result, (1, "level", SyncSituation.S4));
    }

    [Theory]
    [InlineData(12, "axe")]
    [InlineData(18, "bow")]
    public void S4_Scalar_LastWriteByMadeAtWins(long incomingMadeAt, string expected)
    {
        var result = Run(new[] { ProfileRecord(), ItemRecord() },
            Client("c3", 3, OracleOperation.Update, Item, I, made: 14, seen: 10, delivered: 15, ("item_type", "sword", "axe")),
            Client("c1", 1, OracleOperation.Update, Item, I, made: incomingMadeAt, seen: 10, delivered: 20, ("item_type", "sword", "bow")));

        Assert.Equal(expected, result.Value(Item, I, "item_type"));
        AssertClassified(result, (0, "item_type", SyncSituation.S1), (1, "item_type", SyncSituation.S4));
    }

    [Fact]
    public void ChangeThatLostAConflict_DoesNotMakeLaterChangesConflicting()
    {
        var result = Run(new[] { ProfileRecord() },
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 15, ("level", 1L, 4L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 13, seen: 10, delivered: 20, ("level", 1L, 2L)),
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 21, seen: 18, delivered: 25, ("level", 4L, 6L)));

        Assert.Equal(6L, result.Value(Profile, P, "level"));
        AssertClassified(result, (0, "level", SyncSituation.S1), (1, "level", SyncSituation.S4), (2, "level", SyncSituation.S1));
    }

    [Fact]
    public void SameAuthorSecondChangeAfterConflict_IsStillMergedAsConflict()
    {
        var result = Run(new[] { ProfileRecord() },
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 15, ("coins", 100L, 130L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 13, seen: 10, delivered: 20, ("coins", 100L, 110L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 14, seen: 10, delivered: 20, ("coins", 110L, 115L)));

        Assert.Equal(145L, result.Value(Profile, P, "coins"));
        AssertClassified(result, (0, "coins", SyncSituation.S1), (1, "coins", SyncSituation.S4), (2, "coins", SyncSituation.S4));
    }

    [Fact]
    public void SameAuthorChangingFieldTwice_IsNotAConflict()
    {
        var result = Run(new[] { ProfileRecord() },
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 11, seen: 10, delivered: 20, ("coins", 100L, 110L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("coins", 110L, 115L)));

        Assert.Equal(115L, result.Value(Profile, P, "coins"));
        AssertClassified(result, (0, "coins", SyncSituation.S2), (1, "coins", SyncSituation.S2));
    }

    [Fact]
    public void S5_OldClientDeletesRecordWithUnknownData_TombstoneKeepsData()
    {
        var result = Run(new[] { ProfileRecord() },
            Client("c1", 1, OracleOperation.Delete, Profile, P, made: 12, seen: 10, delivered: 20));

        var profile = result.Record(Profile, P);
        Assert.True(profile.IsDeleted);
        Assert.True(profile.IsTombstone);
        Assert.Equal("eu", profile.Values["region"]);
        Assert.Equal("a0", profile.Values["avatar_id"]);
        AssertClassified(result, (0, null, SyncSituation.S5));
    }

    [Fact]
    public void S5_OldClientDeletesRecordWithDependentsInNewerTable_TombstoneKeepsDependents()
    {
        var result = Run(new[] { ProfileRecord(), ItemRecord(durability: 100L), ModificationRecord() },
            Client("c2", 2, OracleOperation.Delete, Item, I, made: 12, seen: 10, delivered: 20));

        Assert.True(result.Record(Item, I).IsTombstone);
        Assert.False(result.Record(Modification, M).IsDeleted);
        AssertClassified(result, (0, null, SyncSituation.S5));
    }

    [Fact]
    public void OldClientDeletesRecordWithoutUnknownData_PlainDelete()
    {
        var result = Run(new[] { ProfileRecord(avatar: null, region: null) },
            Client("c1", 1, OracleOperation.Delete, Profile, P, made: 12, seen: 10, delivered: 20));

        var profile = result.Record(Profile, P);
        Assert.True(profile.IsDeleted);
        Assert.False(profile.IsTombstone);
        AssertClassified(result, (0, null, SyncSituation.S2));
    }

    [Fact]
    public void LatestClientDelete_IsPlainDeleteEvenWithData()
    {
        var result = Run(new[] { ProfileRecord(), ItemRecord(), ModificationRecord() },
            Client("c3", 3, OracleOperation.Delete, Item, I, made: 12, seen: 10, delivered: 20));

        Assert.True(result.Record(Item, I).IsDeleted);
        Assert.False(result.Record(Item, I).IsTombstone);
        AssertClassified(result, (0, null, SyncSituation.S1));
    }

    [Fact]
    public void Insert_ByOldClient_UnknownFieldsGetDefaults_AndChangesToDeletedRecordsAreIgnored()
    {
        var shield = Guid.Parse("00000000-0000-4000-8000-000000000009");
        var result = Run(new[] { ProfileRecord() },
            Client("c1", 1, OracleOperation.Insert, Item, shield, made: 11, seen: 10, delivered: 20,
                ("player_id", null, P), ("item_type", null, "shield"), ("quantity", null, 1L)),
            Server(Item, shield, at: 21, OracleOperation.Delete),
            Client("c1", 1, OracleOperation.Update, Item, shield, made: 15, seen: 10, delivered: 22, ("quantity", 1L, 2L)));

        var item = result.Record(Item, shield);
        Assert.Equal(100L, item.Values["durability"]);
        Assert.Equal(1L, item.Values["quantity"]);
        Assert.True(item.IsDeleted);
        AssertClassified(result, (0, "player_id", SyncSituation.S2), (0, "item_type", SyncSituation.S2), (0, "quantity", SyncSituation.S2));
    }

    [Fact]
    public void MixedClientsV1V2V3AndServer_MatchHandComputedState()
    {
        var result = Run(new[] { ProfileRecord(premium: 10L), ItemRecord() },
            Server(Profile, P, at: 5, ("level", 1L, 2L)),
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 7, seen: 6, delivered: 8, ("region", "eu", "us"), ("coins", 100L, 120L)),
            Client("c2", 2, OracleOperation.Update, Profile, P, made: 6, seen: 4, delivered: 9,
                ("avatar_id", "a0", "a2"), ("premium_currency", 10L, 15L), ("coins", 100L, 105L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 5, seen: 4, delivered: 10, ("nickname", "neo", "trinity"), ("level", 1L, 3L)),
            Client("c1", 1, OracleOperation.Delete, Item, I, made: 6, seen: 4, delivered: 10));

        var profile = result.Record(Profile, P);
        Assert.False(profile.IsDeleted);
        Assert.Equal(new Dictionary<string, object?>
        {
            ["nickname"] = "trinity",
            ["level"] = 2L,
            ["coins"] = 125L,
            ["settings_sound"] = true,
            ["avatar_id"] = "a2",
            ["premium_currency"] = 15L,
            ["region"] = "us",
        }, profile.Values);

        var item = result.Record(Item, I);
        Assert.True(item.IsDeleted);
        Assert.True(item.IsTombstone);
        Assert.Equal(90L, item.Values["durability"]);

        AssertClassified(result,
            (1, "region", SyncSituation.S1),
            (1, "coins", SyncSituation.S1),
            (2, "avatar_id", SyncSituation.S3),
            (2, "premium_currency", SyncSituation.S3),
            (2, "coins", SyncSituation.S4),
            (3, "nickname", SyncSituation.S3),
            (3, "level", SyncSituation.S4),
            (4, null, SyncSituation.S5));
    }

    [Fact]
    public void ClientChangeOutsideKnownSet_IsRejectedAsInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => Run(new[] { ProfileRecord() },
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("region", "eu", "us"))));
    }

    [Fact]
    public void EventsOutOfDeliveryOrder_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => Run(new[] { ProfileRecord() },
            Server(Profile, P, at: 20, ("level", 1L, 2L)),
            Server(Profile, P, at: 15, ("level", 2L, 3L))));
    }

    [Fact]
    public void OracleAssembly_DoesNotReferenceStrategiesOrServer()
    {
        var references = typeof(SyncOracle).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.Contains("Sync.Core", references);
        Assert.DoesNotContain("Sync.Strategies", references);
        Assert.DoesNotContain("Sync.Server", references);
        Assert.DoesNotContain("Sync.Client", references);
    }

    private OracleResult Run(OracleRecord[] initial, params OracleEvent[] events) =>
        _oracle.Run(new OracleInput { InitialRecords = initial.ToList(), Events = events.ToList() });

    private static void AssertClassified(OracleResult result, params (int Event, string? Field, SyncSituation Situation)[] expected) =>
        Assert.Equal(expected, result.Classifications.Select(c => (c.EventIndex, c.Field, c.Situation)));

    private static OracleRecord ProfileRecord(string? avatar = "a0", string? region = "eu", long premium = 0L) =>
        new()
        {
            Table = Profile,
            RecordId = P,
            Values =
            {
                ["nickname"] = "neo", ["level"] = 1L, ["coins"] = 100L, ["settings_sound"] = true,
                ["avatar_id"] = avatar, ["premium_currency"] = premium, ["region"] = region,
            },
        };

    private static OracleRecord ItemRecord(long durability = 90L) =>
        new()
        {
            Table = Item,
            RecordId = I,
            Values = { ["player_id"] = P, ["item_type"] = "sword", ["quantity"] = 1L, ["durability"] = durability },
        };

    private static OracleRecord ModificationRecord() =>
        new()
        {
            Table = Modification,
            RecordId = M,
            Values = { ["item_id"] = I, ["mod_type"] = "sharpness", ["mod_level"] = 1L },
        };

    private static OracleEvent Client(
        string author, int version, OracleOperation operation, string table, Guid id, long made, long seen, long delivered,
        params (string Field, object? Old, object? New)[] changes) =>
        new()
        {
            Author = author,
            SchemaVersion = version,
            Operation = operation,
            Table = table,
            RecordId = id,
            MadeAt = made,
            SeenAt = seen,
            DeliveredAt = delivered,
            Changes = changes.Select(c => new FieldChange { Field = c.Field, OldValue = c.Old, NewValue = c.New }).ToList(),
        };

    private static OracleEvent Server(string table, Guid id, long at, params (string Field, object? Old, object? New)[] changes) =>
        Server(table, id, at, OracleOperation.Update, changes);

    private static OracleEvent Server(
        string table, Guid id, long at, OracleOperation operation, params (string Field, object? Old, object? New)[] changes) =>
        new()
        {
            Author = OracleEvent.ServerAuthor,
            SchemaVersion = SchemaVersions.Latest,
            Operation = operation,
            Table = table,
            RecordId = id,
            MadeAt = at,
            SeenAt = at,
            DeliveredAt = at,
            Changes = changes.Select(c => new FieldChange { Field = c.Field, OldValue = c.Old, NewValue = c.New }).ToList(),
        };
}
