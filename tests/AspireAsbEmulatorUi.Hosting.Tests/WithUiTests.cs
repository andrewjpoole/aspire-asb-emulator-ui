using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.AsbEmulatorUi.Integration;

namespace AspireAsbEmulatorUi.Hosting.Tests;

public class WithUiTests
{
    [Test]
    public async Task WithUi_ShouldReturnEmulatorBuilder()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder
            .AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c.WithUi());

        // Assert - RunAsEmulator returns the service bus resource builder
        await Assert.That(serviceBus).IsNotNull();
        await Assert.That(serviceBus.Resource).IsTypeOf<AzureServiceBusResource>();
    }

    [Test]
    public async Task WithUi_ShouldAddHiddenUiResource()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder
            .AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c.WithUi());

        // Assert - the UI resource should be added to the builder
        var uiResource = builder.Resources.OfType<AsbEmulatorUiResource>().FirstOrDefault();
        await Assert.That(uiResource).IsNotNull();
    }

    [Test]
    public async Task WithUi_ShouldNameUiResourceBasedOnEmulatorName()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder
            .AddAzureServiceBus("myservicebus")
            .RunAsEmulator(c => c.WithUi());

        // Assert
        var uiResource = builder.Resources.OfType<AsbEmulatorUiResource>().FirstOrDefault();
        await Assert.That(uiResource).IsNotNull();
        await Assert.That(uiResource!.Name).IsEqualTo("myservicebus-asb-ui");
    }

    [Test]
    public async Task WithUi_ShouldAddUrlCallbackToEmulatorResource()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Act
        var serviceBus = builder
            .AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c.WithUi());

        // Assert - emulator resource should have URL callback annotations
        var urlCallbacks = serviceBus.Resource.Annotations
            .OfType<ResourceUrlsCallbackAnnotation>();
        await Assert.That(urlCallbacks).IsNotEmpty();
    }

    [Test]
    public async Task WithUi_WithCustomPort_ShouldUseSpecifiedPort()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var customPort = 9000;

        // Act
        var serviceBus = builder
            .AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c.WithUi(httpPort: customPort));

        // Assert
        var uiResource = builder.Resources.OfType<AsbEmulatorUiResource>().FirstOrDefault();
        await Assert.That(uiResource).IsNotNull();

        var endpoints = uiResource!.Annotations
            .OfType<EndpointAnnotation>()
            .Where(e => e.Port == customPort);
        await Assert.That(endpoints).IsNotEmpty();
    }

    [Test]
    public async Task WithUi_ShouldUseDefaultPort8000()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Act
        var serviceBus = builder
            .AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c.WithUi());

        // Assert
        var uiResource = builder.Resources.OfType<AsbEmulatorUiResource>().FirstOrDefault();
        await Assert.That(uiResource).IsNotNull();

        var endpoints = uiResource!.Annotations
            .OfType<EndpointAnnotation>()
            .Where(e => e.Port == 8000);
        await Assert.That(endpoints).IsNotEmpty();
    }

    [Test]
    public async Task WithUi_ShouldBeChainableWithOtherMethods()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Act - ensure WithUi can be chained with other emulator methods
        var serviceBus = builder
            .AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c.WithLifetime(ContainerLifetime.Persistent).WithUi());

        // Assert
        await Assert.That(serviceBus).IsNotNull();
        var uiResource = builder.Resources.OfType<AsbEmulatorUiResource>().FirstOrDefault();
        await Assert.That(uiResource).IsNotNull();
    }

    [Test]
    public async Task WithUi_UiResourceShouldHaveCorrectDockerImage()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Act
        var serviceBus = builder
            .AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c.WithUi());

        // Assert
        var uiResource = builder.Resources.OfType<AsbEmulatorUiResource>().FirstOrDefault();
        await Assert.That(uiResource).IsNotNull();

        var containerAnnotation = uiResource!.Annotations
            .OfType<ContainerImageAnnotation>()
            .FirstOrDefault();
        await Assert.That(containerAnnotation).IsNotNull();
        await Assert.That(containerAnnotation!.Image).IsEqualTo("andrewjpoole/aspireasbemulatorui");
    }

    [Test]
    public async Task WithUi_UiResourceShouldBeExcludedFromManifest()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Act
        var serviceBus = builder
            .AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c.WithUi());

        // Assert
        var uiResource = builder.Resources.OfType<AsbEmulatorUiResource>().FirstOrDefault();
        await Assert.That(uiResource).IsNotNull();

        var manifestExclusion = uiResource!.Annotations
            .OfType<ManifestPublishingCallbackAnnotation>()
            .Any();
        await Assert.That(manifestExclusion).IsTrue();
    }
}
