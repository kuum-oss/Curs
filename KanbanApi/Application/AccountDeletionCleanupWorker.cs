namespace KanbanApi.Application;

public sealed class AccountDeletionCleanupWorker(IServiceScopeFactory scopes, ILogger<AccountDeletionCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().PurgeExpired(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogError("Account deletion cleanup could not finish; it will retry on the next cycle."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
