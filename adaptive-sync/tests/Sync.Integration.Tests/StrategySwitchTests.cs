using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Core.Strategies;
using Sync.Strategies;

namespace Sync.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class StrategySwitchTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string> AllowServer = new() { ["Sync:AllowStrategySwitch"] = "true" };
    private static readonly Dictionary<string, string> AllowResolver = new() { ["Resolver:AllowStrategySwitch"] = "true" };

    [SkippableFact]
    public async Task Switch_IsForbiddenByDefault()
    {
        await using var host = await SyncTestHost.StartAsync(postgres, AdaptiveStrategy.StrategyName);
        var http = host.CreateHttpClient();

        var health = await HealthAsync(http);
        var response = await http.PostAsJsonAsync("admin/strategy", new { strategy = LwwStrategy.StrategyName });

        Assert.False(health.GetProperty("strategySwitch").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(AdaptiveStrategy.StrategyName, (await HealthAsync(http)).GetProperty("strategy").GetString());
    }

    [SkippableFact]
    public async Task BuiltinStrategy_SwitchesInPlaceAndAffectsNextSync()
    {
        await using var host = await SyncTestHost.StartAsync(postgres, AdaptiveStrategy.StrategyName, AllowServer);
        var http = host.CreateHttpClient();

        var health = await HealthAsync(http);
        Assert.True(health.GetProperty("strategySwitch").GetBoolean());
        Assert.Contains(LwwStrategy.StrategyName, health.GetProperty("strategies").EnumerateArray().Select(e => e.GetString()));

        var switched = await http.PostAsJsonAsync("admin/strategy", new { strategy = "lww" });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        Assert.Equal(LwwStrategy.StrategyName, (await HealthAsync(http)).GetProperty("strategy").GetString());

        var scenario = await Dms2Scenario.RunAsync(host);
        Assert.Equal(LwwStrategy.StrategyName, await host.ScalarAsync<string>("SELECT DISTINCT strategy FROM sync_operation_log"));
        var profile = Assert.Single(await host.ServerStateAsync(Dms2Scenario.Profile, SchemaVersions.V3));
        Assert.Null(profile.Values["region"]);
        Assert.Contains(scenario.V1Reconnect.Results[0].Fields, f => f.Field == "region" && f.Outcome == FieldOutcome.Overwritten);

        var unknown = await http.PostAsJsonAsync("admin/strategy", new { strategy = "Nope" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [SkippableFact]
    public async Task ResolverStrategy_SwitchIsForwardedToResolver()
    {
        await using var resolver = new WebApplicationFactory<Sync.Resolver.Program>()
            .WithWebHostBuilder(b => b.UseSetting("Resolver:AllowStrategySwitch", "true"));
        await using var host = await SyncTestHost.StartWithResolverAsync(
            postgres, "http://resolver/", () => resolver.Server.CreateHandler(), AllowServer);
        var http = host.CreateHttpClient();

        var switched = await http.PostAsJsonAsync("admin/strategy", new { strategy = ServerWinsStrategy.StrategyName });

        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        var health = await HealthAsync(http);
        Assert.Equal(ServerWinsStrategy.StrategyName, health.GetProperty("strategy").GetString());
        Assert.Equal("resolver", health.GetProperty("strategySource").GetString());
    }

    [SkippableFact]
    public async Task ResolverWithoutPermission_RefusesForwardedSwitch()
    {
        await using var resolver = new WebApplicationFactory<Sync.Resolver.Program>();
        await using var host = await SyncTestHost.StartWithResolverAsync(
            postgres, "http://resolver/", () => resolver.Server.CreateHandler(), AllowServer);

        var switched = await host.CreateHttpClient().PostAsJsonAsync("admin/strategy", new { strategy = ServerWinsStrategy.StrategyName });

        Assert.Equal(HttpStatusCode.Conflict, switched.StatusCode);
    }

    [SkippableFact]
    public async Task SyncResponse_CarriesSituationAndFieldDecisions()
    {
        await using var host = await SyncTestHost.StartAsync(postgres, AdaptiveStrategy.StrategyName);

        var scenario = await Dms2Scenario.RunAsync(host);

        var profile = scenario.V1Reconnect.Results[0];
        Assert.Equal(SyncSituation.S3, profile.Situation);
        Assert.Contains(profile.Fields, f => f.Field == "nickname" && f.Outcome == FieldOutcome.Applied && f.Situation == SyncSituation.S3);
        Assert.Contains(profile.Fields, f => f.Field == "coins" && f.Outcome == FieldOutcome.Applied);
        Assert.DoesNotContain(profile.Fields, f => f.Outcome == FieldOutcome.Overwritten);
    }

    private static async Task<JsonElement> HealthAsync(HttpClient http) =>
        JsonDocument.Parse(await http.GetStringAsync("health")).RootElement;
}
