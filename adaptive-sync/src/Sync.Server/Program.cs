using Npgsql;
using Sync.Core.Protocol;
using Sync.Core.Resolver;
using Sync.Core.Schema;
using Sync.Server.Migrations;
using Sync.Server.Sync;
using Sync.Strategies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options => SyncJson.Configure(options.SerializerOptions));
builder.Services.AddSingleton(SchemaVersions.Registry);
builder.Services.AddSingleton(sp => NpgsqlDataSource.Create(
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Sync")
    ?? throw new InvalidOperationException("Connection string 'Sync' is not configured.")));
var resolverUrl = builder.Configuration["Sync:ResolverUrl"];
if (string.IsNullOrWhiteSpace(resolverUrl))
{
    builder.Services.AddSingleton<IConflictResolver>(sp => new LocalConflictResolver(
        SyncStrategies.Create(sp.GetRequiredService<IConfiguration>()["Sync:Strategy"] ?? ServerWinsStrategy.StrategyName)));
}
else
{
    builder.Services.AddHttpClient(RemoteConflictResolver.HttpClientName, client =>
    {
        client.BaseAddress = new Uri(resolverUrl.EndsWith('/') ? resolverUrl : resolverUrl + "/");
        client.Timeout = TimeSpan.FromSeconds(5);
    });
    builder.Services.AddSingleton<IConflictResolver>(sp => new RemoteConflictResolver(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(RemoteConflictResolver.HttpClientName),
        sp.GetRequiredService<SchemaRegistry>()));
}
builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddHostedService<MigrationHostedService>();
builder.Services.AddSingleton<SyncService>();

var app = builder.Build();

var allowStrategySwitch = app.Configuration.GetValue("Sync:AllowStrategySwitch", false);

app.MapGet("/health", async (SchemaRegistry registry, SyncService sync, CancellationToken ct) =>
    Results.Ok(new
    {
        status = "ok",
        schemaVersion = registry.LatestVersion,
        strategy = await sync.DescribeStrategyAsync(ct),
        strategySource = sync.StrategySource,
        strategySwitch = allowStrategySwitch,
        strategies = SyncStrategies.Names,
    }));

app.MapPost("/admin/strategy", async (StrategySwitchRequest request, IConflictResolver resolver, SyncService sync, CancellationToken ct) =>
{
    if (!allowStrategySwitch)
        return Results.Json(new { error = "Strategy switching is disabled on this server." }, statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var strategy = await resolver.SwitchAsync(request.Strategy, ct);
        return Results.Ok(new { strategy, strategySource = sync.StrategySource });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (StrategySwitchException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
    }
    catch (ResolverUnavailableException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/sync", async (SyncRequest request, SyncService sync, HttpResponse http, CancellationToken ct) =>
{
    try
    {
        var timing = new SyncTiming();
        var response = await sync.SyncAsync(request, ct, timing);
        http.Headers["Server-Timing"] = timing.ToServerTimingHeader();
        return Results.Ok(response);
    }
    catch (SyncProtocolException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (ResolverUnavailableException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

public partial class Program;
