using Azure;
using AspireAsbEmulatorUi.App.Models;

namespace AspireAsbEmulatorUi.App.Services;

public sealed class ServiceBusEntityRepository(
    ServiceBusAdministrationOptions options,
    ServiceBusAdministrationService administration,
    Func<AsbEmulatorSqlEntityRepository> sqlRepository,
    ILogger<ServiceBusEntityRepository> logger) : IServiceBusEntityRepository
{
    public async Task<List<ServiceBusEntityInfo>> GetEntitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.DiscoveryMode == EntityDiscoveryMode.Sql || !options.IsConfigured)
            return await sqlRepository().GetEntitiesAsync(cancellationToken);

        try
        {
            return await administration.GetEntitiesAsync(cancellationToken);
        }
        catch (Exception ex) when (options.DiscoveryMode == EntityDiscoveryMode.Auto &&
                                   options.HasSqlConfiguration && !cancellationToken.IsCancellationRequested &&
                                   CanFallBackToSql(ex))
        {
            logger.LogWarning("Administration discovery unavailable ({ExceptionType}); using configured legacy SQL discovery.", ex.GetType().Name);
            return await sqlRepository().GetEntitiesAsync(cancellationToken);
        }
    }

    private static bool CanFallBackToSql(Exception exception) => exception is
        NotSupportedException or TimeoutException or HttpRequestException or
        RequestFailedException { Status: 0 or 404 or 405 or 501 or 502 or 503 or 504 };
}
