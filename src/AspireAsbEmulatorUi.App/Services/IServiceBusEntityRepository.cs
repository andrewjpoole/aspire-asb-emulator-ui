using AspireAsbEmulatorUi.App.Models;

namespace AspireAsbEmulatorUi.App.Services;

public interface IServiceBusEntityRepository
{
    Task<List<ServiceBusEntityInfo>> GetEntitiesAsync(CancellationToken cancellationToken = default);
}
