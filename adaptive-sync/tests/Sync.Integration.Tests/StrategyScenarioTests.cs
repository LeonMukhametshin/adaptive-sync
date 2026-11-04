using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Strategies;

namespace Sync.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class StrategyScenarioTests(PostgresFixture postgres)
{
    public static TheoryData<string, OperationOutcome, string, long, string?, string?, long> Expectations => new()
    {
        { ServerWinsStrategy.StrategyName, OperationOutcome.Rejected, "neo", 100L, "a1", "us", 90L },
        { LwwStrategy.StrategyName, OperationOutcome.Applied, "neo_v1", 150L, null, null, 100L },
        { ClientWinsStrategy.StrategyName, OperationOutcome.Applied, "neo_v1", 150L, null, null, 100L },
        { FieldLwwNoSchemaStrategy.StrategyName, OperationOutcome.Applied, "neo_v1", 150L, "a1", "us", 90L },
        { AdaptiveStrategy.StrategyName, OperationOutcome.Applied, "neo_v1", 150L, "a1", "us", 90L },
    };

    [SkippableTheory]
    [MemberData(nameof(Expectations))]
    public async Task Dms2Scenario_RunsThroughSyncEndpoint_OnEachStrategy(
        string strategy, OperationOutcome v1ProfileOutcome, string nickname, long coins, string? avatar, string? region,
        long swordDurability)
    {
        await using var host = await SyncTestHost.StartAsync(postgres, strategy);
        var scenario = await Dms2Scenario.RunAsync(host);

        Dms2Scenario.AssertOutcomes(scenario.V3Reconnect, OperationOutcome.Applied, OperationOutcome.Applied);
        Dms2Scenario.AssertOutcomes(scenario.V1Reconnect,
            v1ProfileOutcome, OperationOutcome.Applied, OperationOutcome.Applied, OperationOutcome.Applied);

        var profile = Assert.Single(await host.ServerStateAsync(Dms2Scenario.Profile, SchemaVersions.V3));
        Assert.Equal(
            Dms2Scenario.Values(("nickname", nickname), ("level", 1L), ("coins", coins), ("settings_sound", true),
                ("avatar_id", avatar), ("premium_currency", 0L), ("region", region)),
            profile.Values);

        var items = (await host.ServerStateAsync(Dms2Scenario.Item, SchemaVersions.V3)).ToDictionary(s => s.RecordId);
        Assert.Equal(5L, items[scenario.SwordId].Values["quantity"]);
        Assert.Equal(swordDurability, items[scenario.SwordId].Values["durability"]);
        Assert.Equal(2L, items[scenario.ShieldId].Values["quantity"]);

        var modification = Assert.Single(await host.ServerStateAsync(Dms2Scenario.Modification, SchemaVersions.V3));
        Assert.Equal(2L, modification.Values["mod_level"]);

        Assert.Equal(strategy, await host.ScalarAsync<string>("SELECT DISTINCT strategy FROM sync_operation_log"));
        await EndToEndSyncTests.AssertClientMatchesServerAsync(host, scenario.V1);
        await EndToEndSyncTests.AssertClientMatchesServerAsync(host, scenario.V3);
        Assert.Equal(0, scenario.V1.PendingOperationCount);
        Assert.Equal(0, scenario.V3.PendingOperationCount);
    }

    [SkippableFact]
    public async Task SecondChangeInSameBatch_StillConflictsWithUnseenForeignChange()
    {
        await using var host = await SyncTestHost.StartAsync(postgres, AdaptiveStrategy.StrategyName);
        var a = host.CreateClient("client-a", SchemaVersions.V3, seed: 11);
        var b = host.CreateClient("client-b", SchemaVersions.V3, seed: 12);
        var profile = a.Insert(Dms2Scenario.Profile, Dms2Scenario.Values(("nickname", "neo"), ("coins", 100), ("level", 1)));
        await a.SyncAsync();
        await b.SyncAsync();

        b.Update(Dms2Scenario.Profile, profile, Dms2Scenario.Values(("level", 5)));
        await b.SyncAsync();

        a.Update(Dms2Scenario.Profile, profile, Dms2Scenario.Values(("level", 2), ("coins", 110)));
        a.Update(Dms2Scenario.Profile, profile, Dms2Scenario.Values(("level", 3)));
        Assert.Equal(2, a.PendingOperationCount);
        var response = await a.SyncAsync();

        Dms2Scenario.AssertOutcomes(response, OperationOutcome.Merged, OperationOutcome.Rejected);
        var state = Assert.Single(await host.ServerStateAsync(Dms2Scenario.Profile, SchemaVersions.V3));
        Assert.Equal(5L, state.Values["level"]);
        Assert.Equal(110L, state.Values["coins"]);
    }
}
