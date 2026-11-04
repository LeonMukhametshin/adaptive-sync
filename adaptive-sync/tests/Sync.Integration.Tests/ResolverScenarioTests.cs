using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Strategies;

namespace Sync.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class ResolverScenarioTests(PostgresFixture postgres)
{
    [SkippableFact]
    public async Task Dms2Scenario_ThroughResolverService_MatchesBuiltinAdaptive()
    {
        await using var resolver = new WebApplicationFactory<Sync.Resolver.Program>();
        await using var host = await SyncTestHost.StartWithResolverAsync(postgres, "http://resolver/", () => resolver.Server.CreateHandler());

        var scenario = await Dms2Scenario.RunAsync(host);

        Dms2Scenario.AssertOutcomes(scenario.V3Reconnect, OperationOutcome.Applied, OperationOutcome.Applied);
        Dms2Scenario.AssertOutcomes(scenario.V1Reconnect,
            OperationOutcome.Applied, OperationOutcome.Applied, OperationOutcome.Applied, OperationOutcome.Applied);

        var profile = Assert.Single(await host.ServerStateAsync(Dms2Scenario.Profile, SchemaVersions.V3));
        Assert.Equal(
            Dms2Scenario.Values(("nickname", "neo_v1"), ("level", 1L), ("coins", 150L), ("settings_sound", true),
                ("avatar_id", "a1"), ("premium_currency", 0L), ("region", "us")),
            profile.Values);
        var items = (await host.ServerStateAsync(Dms2Scenario.Item, SchemaVersions.V3)).ToDictionary(s => s.RecordId);
        Assert.Equal(5L, items[scenario.SwordId].Values["quantity"]);
        Assert.Equal(90L, items[scenario.SwordId].Values["durability"]);
        Assert.Equal(2L, items[scenario.ShieldId].Values["quantity"]);

        Assert.Equal(AdaptiveStrategy.StrategyName, await host.ScalarAsync<string>("SELECT DISTINCT strategy FROM sync_operation_log"));
        Assert.Equal("S3", await host.ScalarAsync<string>(
            "SELECT f->>'situation' FROM sync_operation_log, jsonb_array_elements(fields) f " +
            "WHERE client_id = 'client-v1' AND table_name = 'player_profile' AND f->>'field' = 'nickname'"));
        await EndToEndSyncTests.AssertClientMatchesServerAsync(host, scenario.V1);
        await EndToEndSyncTests.AssertClientMatchesServerAsync(host, scenario.V3);

        using var health = JsonDocument.Parse(await host.CreateHttpClient().GetStringAsync("health"));
        Assert.Equal(AdaptiveStrategy.StrategyName, health.RootElement.GetProperty("strategy").GetString());
        Assert.Equal("resolver", health.RootElement.GetProperty("strategySource").GetString());
    }

    [SkippableFact]
    public async Task UnavailableResolver_FailsWholeRequestWithoutPartialChanges()
    {
        await using var host = await SyncTestHost.StartWithResolverAsync(postgres, "http://127.0.0.1:9/");
        var client = host.CreateClient("client-v3", SchemaVersions.V3, seed: 3);
        var profile = client.Insert(SchemaVersions.PlayerProfile, Dms2Scenario.Values(("nickname", "neo"), ("coins", 100)));
        client.Insert(SchemaVersions.InventoryItem, Dms2Scenario.Values(("player_id", profile), ("item_type", "sword"), ("quantity", 1)));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SyncAsync());

        Assert.Contains("503", error.Message);
        Assert.Contains("Resolver is unavailable", error.Message);
        Assert.Equal(2, client.PendingOperationCount);
        Assert.Empty(await host.ServerStateAsync(SchemaVersions.PlayerProfile, SchemaVersions.V3));
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM sync_operation_log"));

        using var health = JsonDocument.Parse(await host.CreateHttpClient().GetStringAsync("health"));
        Assert.Equal("unavailable", health.RootElement.GetProperty("strategy").GetString());
    }

    [SkippableFact]
    public async Task BuiltinStrategy_IsReportedAsBuiltin()
    {
        await using var host = await SyncTestHost.StartAsync(postgres, AdaptiveStrategy.StrategyName);

        using var health = JsonDocument.Parse(await host.CreateHttpClient().GetStringAsync("health"));

        Assert.Equal(AdaptiveStrategy.StrategyName, health.RootElement.GetProperty("strategy").GetString());
        Assert.Equal("builtin", health.RootElement.GetProperty("strategySource").GetString());
    }
}
