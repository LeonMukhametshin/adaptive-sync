using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Sync.Core.Protocol;
using Sync.Core.Resolver;
using Sync.Core.Schema;
using Sync.Core.Strategies;
using Sync.Strategies;

namespace Sync.Resolver.Tests;

public class ResolverServiceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Guid Id = Guid.Parse("00000000-0000-4000-8000-000000000001");

    private readonly WebApplicationFactory<Program> _factory;

    public ResolverServiceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_ReportsAdaptiveByDefaultAndContractVersion()
    {
        var health = await _factory.CreateClient().GetFromJsonAsync<ResolverHealth>("/health", SyncJson.Options);

        Assert.Equal("ok", health!.Status);
        Assert.Equal(AdaptiveStrategy.StrategyName, health.Strategy);
        Assert.Equal(ResolveContract.Version, health.ContractVersion);
    }

    [Fact]
    public async Task Resolve_ReturnsSameDecisionsAsLocalStrategy()
    {
        var request = new ResolveRequest
        {
            ClientId = "c1",
            SchemaVersion = SchemaVersions.V1,
            Items =
            {
                Item(Update(("nickname", "neo", "trinity"), ("coins", 100L, 110L)), Profile(regionChangedAfterBase: true)),
                Item(Delete(), Profile(regionChangedAfterBase: false)),
                Item(Update(("coins", 100L, 110L)), null),
            },
        };

        var response = await Resolve(_factory.CreateClient(), request);

        var local = new AdaptiveStrategy();
        var context = new SyncContext(SchemaVersions.Registry, "c1", SchemaVersions.V1);
        var expected = request.Items.Select(i => local.Decide(context, i.Operation, i.Current)).ToList();
        Assert.Equal(AdaptiveStrategy.StrategyName, response.Strategy);
        Assert.Equal(expected.Count, response.Decisions.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var actual = response.Decisions[i];
            ResolveValues.Normalize(SchemaVersions.Registry, request.Items[i].Operation.Table, actual);
            Assert.Equal(expected[i].Outcome, actual.Outcome);
            Assert.Equal(expected[i].FieldWrites, actual.FieldWrites);
            Assert.Equal(expected[i].Tombstone, actual.Tombstone);
            Assert.Equal(expected[i].HardDelete, actual.HardDelete);
            Assert.Equal(expected[i].Situation, actual.Situation);
            Assert.Equal(
                expected[i].Fields.Select(f => (f.Field, f.Outcome, f.Situation)),
                actual.Fields.Select(f => (f.Field, f.Outcome, f.Situation)));
        }

        Assert.Equal(new Dictionary<string, object?> { ["nickname"] = "trinity", ["coins"] = 140L }, response.Decisions[0].FieldWrites);
        Assert.Equal(SyncSituation.S5, response.Decisions[1].Situation);
        Assert.Equal(OperationOutcome.Rejected, response.Decisions[2].Outcome);
    }

    [Fact]
    public async Task Resolve_UsesConfiguredStrategy()
    {
        using var serverWins = _factory.WithWebHostBuilder(b => b.UseSetting("Resolver:Strategy", ServerWinsStrategy.StrategyName));
        var request = new ResolveRequest
        {
            ClientId = "c1",
            SchemaVersion = SchemaVersions.V1,
            Items = { Item(Update(("nickname", "neo", "trinity")), Profile(regionChangedAfterBase: true)) },
        };

        var response = await Resolve(serverWins.CreateClient(), request);

        Assert.Equal(ServerWinsStrategy.StrategyName, response.Strategy);
        Assert.Equal(OperationOutcome.Rejected, response.Decisions[0].Outcome);
    }

    [Fact]
    public async Task StrategySwitch_IsForbiddenUnlessAllowed()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/strategy", new StrategySwitchRequest { Strategy = "Lww" }, SyncJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await _factory.CreateClient().GetFromJsonAsync<ResolverHealth>("/health", SyncJson.Options))!.AllowStrategySwitch);
    }

    [Fact]
    public async Task StrategySwitch_WhenAllowed_ChangesStrategyForNextResolve()
    {
        using var switchable = _factory.WithWebHostBuilder(b => b.UseSetting("Resolver:AllowStrategySwitch", "true"));
        var client = switchable.CreateClient();

        var switched = await client.PostAsJsonAsync("/strategy", new StrategySwitchRequest { Strategy = "clientwins" }, SyncJson.Options);
        var unknown = await client.PostAsJsonAsync("/strategy", new StrategySwitchRequest { Strategy = "Nope" }, SyncJson.Options);
        var response = await Resolve(client, new ResolveRequest
        {
            ClientId = "c1",
            SchemaVersion = SchemaVersions.V1,
            Items = { Item(Update(("nickname", "neo", "trinity")), Profile(regionChangedAfterBase: true)) },
        });

        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(ClientWinsStrategy.StrategyName, response.Strategy);
        Assert.Contains(response.Decisions[0].Fields, f => f.Field == "region" && f.Outcome == FieldOutcome.Overwritten);
        Assert.True((await client.GetFromJsonAsync<ResolverHealth>("/health", SyncJson.Options))!.AllowStrategySwitch);
    }

    [Theory]
    [InlineData(2, 1, "Contract version")]
    [InlineData(1, 9, "Schema version")]
    public async Task Resolve_RejectsUnsupportedVersions(int contract, int schema, string error)
    {
        var request = new ResolveRequest { ContractVersion = contract, ClientId = "c1", SchemaVersion = schema };

        var message = await _factory.CreateClient().PostAsJsonAsync("/resolve", request, SyncJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, message.StatusCode);
        Assert.Contains(error, await message.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Resolve_UnknownField_IsBadRequest()
    {
        var request = new ResolveRequest
        {
            ClientId = "c1",
            SchemaVersion = SchemaVersions.V1,
            Items = { Item(Update(("missing", 1L, 2L)), null) },
        };

        var message = await _factory.CreateClient().PostAsJsonAsync("/resolve", request, SyncJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, message.StatusCode);
    }

    private static async Task<ResolveResponse> Resolve(HttpClient client, ResolveRequest request)
    {
        var message = await client.PostAsJsonAsync("/resolve", request, SyncJson.Options);
        message.EnsureSuccessStatusCode();
        return (await message.Content.ReadFromJsonAsync<ResolveResponse>(SyncJson.Options))!;
    }

    private static ResolveItem Item(SyncOperation operation, ServerRecord? current) => new() { Operation = operation, Current = current };

    private static ServerRecord Profile(bool regionChangedAfterBase)
    {
        var record = new ServerRecord
        {
            Table = SchemaVersions.PlayerProfile,
            RecordId = Id,
            RowVersion = 8,
            Values =
            {
                ["nickname"] = "neo", ["level"] = 1L, ["coins"] = 130L, ["settings_sound"] = true,
                ["avatar_id"] = "a0", ["premium_currency"] = 0L, ["region"] = "us",
            },
        };
        foreach (var field in record.Values.Keys)
            record.FieldVersions[field] = new FieldVersion(5, 10);
        record.FieldVersions["coins"] = new FieldVersion(8, 15);
        if (regionChangedAfterBase)
            record.FieldVersions["region"] = new FieldVersion(8, 15);
        return record;
    }

    private static SyncOperation Update(params (string Field, object? Old, object? New)[] changes) =>
        new()
        {
            OpId = Guid.NewGuid(),
            Table = SchemaVersions.PlayerProfile,
            RecordId = Id,
            Type = OperationType.Update,
            BaseRowVersion = 5,
            LogicalTime = 20,
            Changes = changes.Select(c => new FieldChange { Field = c.Field, OldValue = c.Old, NewValue = c.New }).ToList(),
        };

    private static SyncOperation Delete()
    {
        var operation = Update();
        operation.Type = OperationType.Delete;
        return operation;
    }
}
