using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Oracle;

namespace Sync.Simulation;

public sealed record Scenario(string Name, OracleInput Input);

public static class OracleScenarios
{
    public const string Profile = SchemaVersions.PlayerProfile;
    public const string Item = SchemaVersions.InventoryItem;
    public const string Modification = SchemaVersions.ItemModification;

    public static readonly Guid P = Guid.Parse("00000000-0000-4000-8000-000000000001");
    public static readonly Guid I = Guid.Parse("00000000-0000-4000-8000-000000000002");
    public static readonly Guid M = Guid.Parse("00000000-0000-4000-8000-000000000003");
    public static readonly Guid Shield = Guid.Parse("00000000-0000-4000-8000-000000000009");

    public static IReadOnlyList<Scenario> All { get; } = new[]
    {
        Build("S1 latest client, no conflict", new[] { ProfileRecord() },
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("coins", 100L, 120L))),
        Build("S2 old client, no conflict", new[] { ProfileRecord() },
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("nickname", "neo", "trinity"))),
        Build("S3 unknown field changed on server", new[] { ProfileRecord() },
            Server(Profile, P, at: 15, ("region", "eu", "us")),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("nickname", "neo", "trinity"))),
        Build("S2 unknown field change already seen", new[] { ProfileRecord() },
            Server(Profile, P, at: 5, ("region", "eu", "us")),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("nickname", "neo", "trinity"))),
        Build("S4 counter", new[] { ProfileRecord() },
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 14, seen: 10, delivered: 15, ("coins", 100L, 130L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("coins", 100L, 110L))),
        Build("S4 user_owned", new[] { ProfileRecord() },
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 14, seen: 10, delivered: 15, ("nickname", "neo", "morpheus")),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("nickname", "neo", "trinity"))),
        Build("S4 user_preference", new[] { ProfileRecord() },
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 14, seen: 10, delivered: 15, ("settings_sound", true, false)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("settings_sound", true, true))),
        Build("S4 server_authoritative", new[] { ProfileRecord() },
            Server(Profile, P, at: 15, ("level", 1L, 5L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("level", 1L, 2L))),
        Build("S4 scalar, server newer", new[] { ProfileRecord(), ItemRecord() },
            Client("c3", 3, OracleOperation.Update, Item, I, made: 14, seen: 10, delivered: 15, ("item_type", "sword", "axe")),
            Client("c1", 1, OracleOperation.Update, Item, I, made: 12, seen: 10, delivered: 20, ("item_type", "sword", "bow"))),
        Build("S4 scalar, client newer", new[] { ProfileRecord(), ItemRecord() },
            Client("c3", 3, OracleOperation.Update, Item, I, made: 14, seen: 10, delivered: 15, ("item_type", "sword", "axe")),
            Client("c1", 1, OracleOperation.Update, Item, I, made: 18, seen: 10, delivered: 20, ("item_type", "sword", "bow"))),
        Build("Same author changes a field twice", new[] { ProfileRecord() },
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 11, seen: 10, delivered: 20, ("coins", 100L, 110L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 20, ("coins", 110L, 115L))),
        Build("Lost change does not create later conflicts", new[] { ProfileRecord() },
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 15, ("level", 1L, 4L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 13, seen: 10, delivered: 20, ("level", 1L, 2L)),
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 21, seen: 18, delivered: 25, ("level", 4L, 6L))),
        Build("Second own change after conflict is merged again", new[] { ProfileRecord() },
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 12, seen: 10, delivered: 15, ("coins", 100L, 130L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 13, seen: 10, delivered: 20, ("coins", 100L, 110L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 14, seen: 10, delivered: 20, ("coins", 110L, 115L))),
        Build("S5 delete with unknown data", new[] { ProfileRecord() },
            Client("c1", 1, OracleOperation.Delete, Profile, P, made: 12, seen: 10, delivered: 20)),
        Build("S5 delete with dependents in newer table", new[] { ProfileRecord(), ItemRecord(durability: 100L), ModificationRecord() },
            Client("c2", 2, OracleOperation.Delete, Item, I, made: 12, seen: 10, delivered: 20)),
        Build("Old client delete without unknown data", new[] { ProfileRecord(avatar: null, region: null) },
            Client("c1", 1, OracleOperation.Delete, Profile, P, made: 12, seen: 10, delivered: 20)),
        Build("Latest client delete", new[] { ProfileRecord(), ItemRecord(), ModificationRecord() },
            Client("c3", 3, OracleOperation.Delete, Item, I, made: 12, seen: 10, delivered: 20)),
        Build("Insert by old client, then server delete", new[] { ProfileRecord() },
            Client("c1", 1, OracleOperation.Insert, Item, Shield, made: 11, seen: 10, delivered: 20,
                ("player_id", null, P), ("item_type", null, "shield"), ("quantity", null, 1L)),
            Server(Item, Shield, at: 21, OracleOperation.Delete),
            Client("c1", 1, OracleOperation.Update, Item, Shield, made: 15, seen: 10, delivered: 22, ("quantity", 1L, 2L))),
        Build("Mixed v1, v2, v3 and server", new[] { ProfileRecord(premium: 10L), ItemRecord() },
            Server(Profile, P, at: 5, ("level", 1L, 2L)),
            Client("c3", 3, OracleOperation.Update, Profile, P, made: 7, seen: 6, delivered: 8, ("region", "eu", "us"), ("coins", 100L, 120L)),
            Client("c2", 2, OracleOperation.Update, Profile, P, made: 6, seen: 4, delivered: 9,
                ("avatar_id", "a0", "a2"), ("premium_currency", 10L, 15L), ("coins", 100L, 105L)),
            Client("c1", 1, OracleOperation.Update, Profile, P, made: 5, seen: 4, delivered: 10, ("nickname", "neo", "trinity"), ("level", 1L, 3L)),
            Client("c1", 1, OracleOperation.Delete, Item, I, made: 6, seen: 4, delivered: 10)),
    };

    public static OracleRecord ProfileRecord(string? avatar = "a0", string? region = "eu", long premium = 0L) =>
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

    public static OracleRecord ItemRecord(long durability = 90L) =>
        new()
        {
            Table = Item,
            RecordId = I,
            Values = { ["player_id"] = P, ["item_type"] = "sword", ["quantity"] = 1L, ["durability"] = durability },
        };

    public static OracleRecord ModificationRecord() =>
        new()
        {
            Table = Modification,
            RecordId = M,
            Values = { ["item_id"] = I, ["mod_type"] = "sharpness", ["mod_level"] = 1L },
        };

    public static OracleEvent Client(
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
            Changes = Changes(changes),
        };

    public static OracleEvent Server(string table, Guid id, long at, params (string Field, object? Old, object? New)[] changes) =>
        Server(table, id, at, OracleOperation.Update, changes);

    public static OracleEvent Server(
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
            Changes = Changes(changes),
        };

    private static Scenario Build(string name, OracleRecord[] initial, params OracleEvent[] events) =>
        new(name, new OracleInput { InitialRecords = initial.ToList(), Events = events.ToList() });

    private static List<FieldChange> Changes((string Field, object? Old, object? New)[] changes) =>
        changes.Select(c => new FieldChange { Field = c.Field, OldValue = c.Old, NewValue = c.New }).ToList();
}
