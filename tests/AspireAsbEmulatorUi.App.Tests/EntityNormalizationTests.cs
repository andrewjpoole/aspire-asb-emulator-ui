using TUnit;

namespace AspireAsbEmulatorUi.App.Tests;

public class EntityNormalizationTests
{
    [Test]
    public async Task StripNamespacePrefix_Removes_Queue()
    {
        var input = "SBEMULATORNS:QUEUE:MyQueue";
        var result = AspireAsbEmulatorUi.App.Services.AsbEmulatorSqlEntityRepository.StripNamespacePrefix(input);
        await Assert.That(result).IsEqualTo("MyQueue");
    }

    [Test]
    public async Task StripNamespacePrefix_Removes_Topic()
    {
        var input = "SBEMULATORNS:TOPIC:Foo.Bar";
        var result = AspireAsbEmulatorUi.App.Services.AsbEmulatorSqlEntityRepository.StripNamespacePrefix(input);
        await Assert.That(result).IsEqualTo("Foo.Bar");
    }

    [Test]
    public async Task StripNamespacePrefix_NoPrefix_ReturnsSame()
    {
        var input = "OTHER:TOPIC:Name";
        var result = AspireAsbEmulatorUi.App.Services.AsbEmulatorSqlEntityRepository.StripNamespacePrefix(input);
        await Assert.That(result).IsEqualTo("OTHER:TOPIC:Name");
    }

    [Test]
    public async Task CleanEntityName_Preserves_Leading_Dot()
    {
        var input = "SBEMULATORNS:TOPIC:.Leading.Dot";
        var cleaned = AspireAsbEmulatorUi.App.Services.ServiceBusService.CleanEntityName(input);
        await Assert.That(cleaned).IsEqualTo(".leading.dot");
    }

    [Test]
    public async Task CleanEntityName_Subscription_DeadLetter_Returns_DlqPath()
    {
        var input = "SBEMULATORNS:TOPIC:TopicName|SubName/$DeadLetterQueue";
        var cleaned = AspireAsbEmulatorUi.App.Services.ServiceBusService.CleanEntityName(input);
        await Assert.That(cleaned).IsEqualTo("topicname/subscriptions/subname/$DeadLetterQueue");
    }
}
