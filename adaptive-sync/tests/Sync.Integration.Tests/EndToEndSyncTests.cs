using System.Text.Json;
using Sync.Client;
using Sync.Core.Protocol;
using Sync.Core.Schema;

namespace Sync.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class EndToEndSyncTests(PostgresFixture postgres)
{
    private const string Profile = SchemaVersions.PlayerProfile;
    private const string Item = SchemaVersions.InventoryItem;
    private const string Modification = SchemaVersions.ItemModification;

    [SkippableFact]
    public async Task V1AndV3Clients_SyncAfterOffline_ServerWins()
    {
        await using var host = await SyncTestHost.StartAsync(postgres);
        var scenario = await Dms2Scenario.RunAsync(host);
        var (c1, c3) = (scenario.V1, scenario.V3);
        var (profile, sword, shield) = (scenario.ProfileId, scenario.SwordId, scenario.ShieldId);
        var regionChangedAt = scenario.RegionChangedAt;

        AssertOutcomes(scenario.V3Reconnect, OperationOutcome.Applied, OperationOutcome.Applied);
        AssertOutcomes(scenario.V1Reconnect,
            OperationOutcome.Rejected, OperationOutcome.Applied, OperationOutcome.Applied, OperationOutcome.Applied);
        Assert.Contains("Conflict", scenario.V1Reconnect.Results[0].Message);

        var serverProfile = Assert.Single(await host.ServerStateAsync(Profile, SchemaVersions.V3));
        Assert.Equal(
            Values(("nickname", "neo"), ("level", 1L), ("coins", 100L), ("settings_sound", true), ("avatar_id", "a1"),
                ("premium_currency", 0L), ("region", "us")),
            serverProfile.Values);

        var serverItems = (await host.ServerStateAsync(Item, SchemaVersions.V3)).ToDictionary(s => s.RecordId);
        Assert.Equal(Values(("player_id", profile), ("item_type", "sword"), ("quantity", 5L), ("durability", 90L)), serverItems[sword].Values);
        Assert.Equal(Values(("player_id", profile), ("item_type", "shield"), ("quantity", 2L), ("durability", 100L)), serverItems[shield].Values);

        var serverMod = Assert.Single(await host.ServerStateAsync(Modification, SchemaVersions.V3));
        Assert.Equal(2L, serverMod.Values["mod_level"]);

        await AssertClientMatchesServerAsync(host, c1);
        await AssertClientMatchesServerAsync(host, c3);
        Assert.Equal(0, c1.PendingOperationCount);
        Assert.Equal(0, c3.PendingOperationCount);

        var fieldVersions = JsonDocument.Parse(await host.ScalarAsync<string>(
            "SELECT field_versions::text FROM player_profile WHERE id = $1", profile)).RootElement;
        Assert.Equal(regionChangedAt, fieldVersions.GetProperty("region").GetProperty("t").GetInt64());
        Assert.Equal(serverProfile.RowVersion, fieldVersions.GetProperty("region").GetProperty("v").GetInt64());
        Assert.True(fieldVersions.GetProperty("nickname").GetProperty("v").GetInt64() < serverProfile.RowVersion);
    }

    [SkippableFact]
    public async Task ResendingSameBatch_DoesNotDuplicateChanges()
    {
        await using var host = await SyncTestHost.StartAsync(postgres);
        var recorder = new RecordingTransport(host.CreateTransport());
        var client = host.CreateClient("client-v1", SchemaVersions.V1, seed: 11, recorder);

        var profile = client.Insert(Profile, Values(("nickname", "neo"), ("coins", 10)));
        client.Update(Profile, profile, Values(("coins", 25)));
        client.Insert(Item, Values(("player_id", profile), ("item_type", "sword"), ("quantity", 1)));
        var first = await client.SyncAsync();

        var stateBefore = await host.ServerStateAsync(Profile, SchemaVersions.V3);
        var itemsBefore = await host.ServerStateAsync(Item, SchemaVersions.V3);
        var loggedBefore = await host.ScalarAsync<long>("SELECT count(*) FROM sync_operation_log");

        var replay = await host.CreateTransport().SendAsync(Assert.Single(recorder.Requests));

        Assert.Equal(
            first.Results.Select(r => (r.OpId, r.Outcome, r.RowVersion)),
            replay.Results.Select(r => (r.OpId, r.Outcome, r.RowVersion)));
        Assert.Equal(first.ServerVersion, replay.ServerVersion);
        Assert.Equal(3, loggedBefore);
        Assert.Equal(loggedBefore, await host.ScalarAsync<long>("SELECT count(*) FROM sync_operation_log"));
        AssertSameStates(stateBefore, await host.ServerStateAsync(Profile, SchemaVersions.V3));
        AssertSameStates(itemsBefore, await host.ServerStateAsync(Item, SchemaVersions.V3));
        Assert.Equal(25L, Assert.Single(stateBefore).Values["coins"]);
    }

    [SkippableFact]
    public async Task LostResponse_ResentWithNewOperations_IsAppliedOnce()
    {
        await using var host = await SyncTestHost.StartAsync(postgres);
        var lossy = new LostResponseTransport(host.CreateTransport());
        var client = host.CreateClient("client-v3", SchemaVersions.V3, seed: 21, lossy);

        var profile = client.Insert(Profile, Values(("nickname", "neo"), ("coins", 10)));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SyncAsync());
        Assert.Equal(1, client.PendingOperationCount);

        client.Update(Profile, profile, Values(("coins", 40)));
        var response = await client.SyncAsync();

        AssertOutcomes(response, OperationOutcome.Applied, OperationOutcome.Applied);
        Assert.Equal(2, await host.ScalarAsync<long>("SELECT count(*) FROM sync_operation_log"));
        var serverProfile = Assert.Single(await host.ServerStateAsync(Profile, SchemaVersions.V3));
        Assert.Equal(40L, serverProfile.Values["coins"]);
        await AssertClientMatchesServerAsync(host, client);
    }

    [SkippableFact]
    public async Task FieldOutsideKnownSet_IsRejectedAsProtocolError()
    {
        await using var host = await SyncTestHost.StartAsync(postgres);
        var request = new SyncRequest
        {
            ClientId = "raw-v1",
            SchemaVersion = SchemaVersions.V1,
            Operations =
            {
                new SyncOperation
                {
                    OpId = Guid.Parse("10000000-0000-4000-8000-000000000001"),
                    Table = Profile,
                    RecordId = Guid.Parse("20000000-0000-4000-8000-000000000001"),
                    Type = OperationType.Insert,
                    LogicalTime = 1,
                    Changes = { new FieldChange { Field = "region", NewValue = "eu" } },
                },
            },
        };

        var response = await host.CreateTransport().SendAsync(request);

        var result = Assert.Single(response.Results);
        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Contains("outside K(v1)", result.Message);
        Assert.Empty(await host.ServerStateAsync(Profile, SchemaVersions.V3));
    }

    [SkippableFact]
    public async Task UnsupportedSchemaVersion_IsBadRequest()
    {
        await using var host = await SyncTestHost.StartAsync(postgres);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            host.CreateTransport().SendAsync(new SyncRequest { ClientId = "x", SchemaVersion = 9 }));
        Assert.Contains("400", ex.Message);
    }

    internal static async Task AssertClientMatchesServerAsync(SyncTestHost host, SyncClient client)
    {
        foreach (var table in client.Registry.TablesFor(client.SchemaVersion))
        {
            var server = await host.ServerStateAsync(table.Name, client.SchemaVersion);
            var local = client.GetAll(table.Name, includeDeleted: true).OrderBy(s => s.RecordId).ToList();
            AssertSameStates(server, local);
        }
    }

    internal static void AssertSameStates(IReadOnlyList<RecordState> expected, IReadOnlyList<RecordState> actual)
    {
        Assert.Equal(
            expected.Select(s => (s.Table, s.RecordId, s.RowVersion, s.IsDeleted)),
            actual.Select(s => (s.Table, s.RecordId, s.RowVersion, s.IsDeleted)));
        for (var i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i].Values, actual[i].Values);
    }

    private static void AssertOutcomes(SyncResponse response, params OperationOutcome[] expected) =>
        Assert.Equal(expected, response.Results.Select(r => r.Outcome));

    private static Dictionary<string, object?> Values(params (string Field, object? Value)[] values) =>
        values.ToDictionary(v => v.Field, v => v.Value is int i ? (long)i : v.Value);
}
