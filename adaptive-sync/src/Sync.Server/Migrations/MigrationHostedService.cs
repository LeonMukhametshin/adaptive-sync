namespace Sync.Server.Migrations;

public sealed class MigrationHostedService(MigrationRunner runner, ILogger<MigrationHostedService> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var applied = await runner.MigrateAsync(cancellationToken);
        logger.LogInformation("Applied {Count} database migrations", applied);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
