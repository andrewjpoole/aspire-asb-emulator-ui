using NSubstitute;
using AspireAsbEmulatorUi.App.Services;
using AspireAsbEmulatorUi.App.Models;

namespace AspireAsbEmulatorUi.App.Tests;

public class EntityExistsViaRepoTests
{
    [Test]
    public async Task EntityExists_Resolves_Dotted_Topic()
    {
        var repo = Substitute.For<AsbEmulatorSqlEntityRepository>();
        var entities = new List<ServiceBusEntityInfo>
        {
            new ServiceBusEntityInfo { Name = "FIM1437.PAYMENTS.CORRESPONDENTBANK.VIRTUALACCOUNTNEWVERSIONEVENT", EntityType = "Topic" }
        };
        repo.GetEntitiesAsync().Returns(Task.FromResult(entities));

        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ServiceBusService>>();
        var svc = new ServiceBusService(string.Empty, logger, repo);

        var cleaned = ServiceBusService.CleanEntityName("SBEMULATORNS:TOPIC:FIM1437.PAYMENTS.CORRESPONDENTBANK.VIRTUALACCOUNTNEWVERSIONEVENT");
        var result = await svc.EntityExistsViaRepoAsync(cleaned);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task EntityExists_Resolves_Subscription()
    {
        var repo = Substitute.For<AsbEmulatorSqlEntityRepository>();
        var entities = new List<ServiceBusEntityInfo>
        {
            new ServiceBusEntityInfo { Name = "TopicName|SubName", EntityType = "Subscription" }
        };
        repo.GetEntitiesAsync().Returns(Task.FromResult(entities));

        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ServiceBusService>>();
        var svc = new ServiceBusService(string.Empty, logger, repo);

        var cleaned = ServiceBusService.CleanEntityName("SBEMULATORNS:TOPIC:TopicName|SubName");
        var exists = await svc.EntityExistsViaRepoAsync(cleaned);
        await Assert.That(exists).IsTrue();
    }

    [Test]
    public async Task EntityExists_Resolves_DLQ()
    {
        var repo = Substitute.For<AsbEmulatorSqlEntityRepository>();
        var entities = new List<ServiceBusEntityInfo>
        {
            new ServiceBusEntityInfo { Name = "TopicName|SubName", EntityType = "Subscription" }
        };
        repo.GetEntitiesAsync().Returns(Task.FromResult(entities));

        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ServiceBusService>>();
        var svc = new ServiceBusService(string.Empty, logger, repo);

        var cleaned = ServiceBusService.CleanEntityName("SBEMULATORNS:TOPIC:TopicName|SubName/$DeadLetterQueue");
        var exists3 = await svc.EntityExistsViaRepoAsync(cleaned);
        await Assert.That(exists3).IsTrue();
    }
}
