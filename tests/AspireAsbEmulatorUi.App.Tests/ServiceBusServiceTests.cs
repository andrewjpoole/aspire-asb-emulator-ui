namespace AspireAsbEmulatorUi.App.Tests;

public class ServiceBusServiceTests
{
    [Test]
    public async Task CleanEntityName_Preserves_Dots_And_Strips_Prefixes()
    {
        var input = "SBEMULATORNS:TOPIC:FIM1437.PAYMENTS.CORRESPONDENTBANK.VIRTUALACCOUNTNEWVERSIONEVENT";
        var cleaned = Services.ServiceBusService.CleanEntityName(input);

        await Assert.That(cleaned).IsEqualTo("fim1437.payments.correspondentbank.virtualaccountnewversionevent");
    }

    [Test]
    public async Task CleanEntityName_ForSubscription_Format()
    {
        var input = "SBEMULATORNS:TOPIC:MyTopic|MySub";
        var cleaned = Services.ServiceBusService.CleanEntityName(input);
        await Assert.That(cleaned).IsEqualTo("mytopic/subscriptions/mysub");
    }

    [Test]
    public async Task CleanEntityNameForComparison_Lowercases()
    {
        var input = "MyQueue";
        var comp = Services.ServiceBusService.CleanEntityNameForComparison(input);
        await Assert.That(comp).IsEqualTo("myqueue");
    }
}
