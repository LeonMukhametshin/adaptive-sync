using Sync.Client;
using Sync.Core.Protocol;
using Sync.Core.Schema;

namespace Sync.Integration.Tests;

public sealed class Dms2Scenario
{
    public const string Profile = SchemaVersions.PlayerProfile;
    public const string Item = SchemaVersions.InventoryItem;
    public const string Modification = SchemaVersions.ItemModification;

    private Dms2Scenario(SyncClient v1, SyncClient v3)
    {
        V1 = v1;
        V3 = v3;
    }

    public SyncClient V1 { get; }

    public SyncClient V3 { get; }

    public Guid ProfileId { get; private set; }

    public Guid SwordId { get; private set; }

    public Guid ShieldId { get; private set; }

    public Guid ModificationId { get; private set; }

    public long RegionChangedAt { get; private set; }

    public SyncResponse V3Reconnect { get; private set; } = new();

    public SyncResponse V1Reconnect { get; private set; } = new();

    public static async Task<Dms2Scenario> RunAsync(SyncTestHost host)
    {
        var scenario = new Dms2Scenario(
            host.CreateClient("client-v1", SchemaVersions.V1, seed: 1),
            host.CreateClient("client-v3", SchemaVersions.V3, seed: 3));
        await scenario.PlayAsync(host);
        return scenario;
    }

    private async Task PlayAsync(SyncTestHost host)
    {
        ProfileId = V3.Insert(Profile, Values(("nickname", "neo"), ("coins", 100), ("avatar_id", "a0"), ("region", "eu")));
        SwordId = V3.Insert(Item, Values(("player_id", ProfileId), ("item_type", "sword"), ("quantity", 1), ("durability", 90)));
        ModificationId = V3.Insert(Modification, Values(("item_id", SwordId), ("mod_type", "sharpness"), ("mod_level", 1)));
        AssertOutcomes(await V3.SyncAsync(), OperationOutcome.Applied, OperationOutcome.Applied, OperationOutcome.Applied);

        await V1.SyncAsync();
        var seenByV1 = V1.Find(Profile, ProfileId)!;
        Assert.Equal("neo", seenByV1.Values["nickname"]);
        Assert.False(seenByV1.Values.ContainsKey("region"));
        Assert.False(V1.Find(Item, SwordId)!.Values.ContainsKey("durability"));

        V1.IsOnline = false;
        V3.IsOnline = false;

        V3.Update(Profile, ProfileId, Values(("region", "us"), ("avatar_id", "a1")));
        RegionChangedAt = host.Clock.Now;
        V3.Update(Modification, ModificationId, Values(("mod_level", 2)));

        V1.Update(Profile, ProfileId, Values(("nickname", "neo_v1"), ("coins", 150)));
        V1.Update(Item, SwordId, Values(("quantity", 5)));
        ShieldId = V1.Insert(Item, Values(("player_id", ProfileId), ("item_type", "shield"), ("quantity", 1)));
        V1.Update(Item, ShieldId, Values(("quantity", 2)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => V1.SyncAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => V3.SyncAsync());
        Assert.Equal(4, V1.PendingOperationCount);
        Assert.Equal(2, V3.PendingOperationCount);

        V3.IsOnline = true;
        V3Reconnect = await V3.SyncAsync();

        V1.IsOnline = true;
        V1Reconnect = await V1.SyncAsync();

        await V3.SyncAsync();
    }

    public static void AssertOutcomes(SyncResponse response, params OperationOutcome[] expected) =>
        Assert.Equal(expected, response.Results.Select(r => r.Outcome));

    public static Dictionary<string, object?> Values(params (string Field, object? Value)[] values) =>
        values.ToDictionary(v => v.Field, v => v.Value is int i ? (long)i : v.Value);
}
