using Sync.Core.Protocol;
using Sync.Core.Resolver;
using Sync.Core.Schema;
using Sync.Core.Strategies;
using Sync.Strategies;

namespace Sync.Resolver;

public sealed class StrategyHolder(ISyncStrategy initial)
{
    private ISyncStrategy _current = initial;

    public ISyncStrategy Current => Volatile.Read(ref _current);

    public void Switch(ISyncStrategy strategy) => Volatile.Write(ref _current, strategy);
}

public sealed class Program
{
    public const string DefaultStrategy = AdaptiveStrategy.StrategyName;

    public static void Main(string[] args) => Build(args).Run();

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.ConfigureHttpJsonOptions(options => SyncJson.Configure(options.SerializerOptions));
        builder.Services.AddSingleton(SchemaVersions.Registry);
        builder.Services.AddSingleton(sp => new StrategyHolder(
            SyncStrategies.Create(sp.GetRequiredService<IConfiguration>()["Resolver:Strategy"] ?? DefaultStrategy)));

        var app = builder.Build();
        var allowSwitch = app.Configuration.GetValue("Resolver:AllowStrategySwitch", false);

        app.MapGet("/" + ResolveContract.HealthPath, (StrategyHolder holder) =>
            Results.Ok(new ResolverHealth
            {
                Status = "ok",
                Strategy = holder.Current.Name,
                ContractVersion = ResolveContract.Version,
                AllowStrategySwitch = allowSwitch,
            }));

        app.MapPost("/" + ResolveContract.StrategyPath, (StrategySwitchRequest request, StrategyHolder holder) =>
        {
            if (!allowSwitch)
                return Results.Json(new { error = "Strategy switching is disabled." }, statusCode: StatusCodes.Status403Forbidden);
            try
            {
                holder.Switch(SyncStrategies.Create(request.Strategy));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            return Results.Ok(new ResolverHealth
            {
                Status = "ok",
                Strategy = holder.Current.Name,
                ContractVersion = ResolveContract.Version,
                AllowStrategySwitch = true,
            });
        });

        app.MapPost("/" + ResolveContract.ResolvePath, (ResolveRequest request, SchemaRegistry registry, StrategyHolder holder) =>
        {
            if (request.ContractVersion != ResolveContract.Version)
                return Results.BadRequest(new { error = $"Contract version {request.ContractVersion} is not supported." });
            if (!registry.IsSupported(request.SchemaVersion))
                return Results.BadRequest(new { error = $"Schema version {request.SchemaVersion} is not supported." });

            var strategy = holder.Current;
            var context = new SyncContext(registry, request.ClientId, request.SchemaVersion);
            var response = new ResolveResponse { Strategy = strategy.Name };
            try
            {
                foreach (var item in request.Items)
                {
                    ResolveValues.Normalize(registry, item);
                    response.Decisions.Add(strategy.Decide(context, item.Operation, item.Current));
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            return Results.Ok(response);
        });

        return app;
    }
}
