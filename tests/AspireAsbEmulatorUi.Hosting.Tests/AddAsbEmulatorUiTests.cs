using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.AsbEmulatorUi.Integration;

namespace AspireAsbEmulatorUi.Hosting.Tests;

public class AddAsbEmulatorUiTests
{
    [Test]
    public async Task AddAsbEmulatorUi_ShouldCreateResource()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();

        // Act
        var uiResource = builder.AddAsbEmulatorUi("asb-ui", serviceBus);

        // Assert
        await Assert.That(uiResource).IsNotNull();
        await Assert.That(uiResource.Resource.Name).IsEqualTo("asb-ui");
    }

    [Test]
    public async Task AddAsbEmulatorUi_WithCustomPort_ShouldUseSpecifiedPort()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();
        var customPort = 9000;

        // Act
        var uiResource = builder.AddAsbEmulatorUi("asb-ui", serviceBus, httpPort: customPort);

        // Assert
        await Assert.That(uiResource).IsNotNull();
        
        var endpoints = uiResource.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Where(e => e.Port == customPort);
        
        await Assert.That(endpoints).IsNotEmpty();
    }

    [Test]
    public async Task AddAsbEmulatorUi_WithDefaultPort_ShouldUse8000()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();

        // Act
        var uiResource = builder.AddAsbEmulatorUi("asb-ui", serviceBus);

        // Assert
        await Assert.That(uiResource).IsNotNull();
        
        var endpoints = uiResource.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Where(e => e.Port == 8000);
        
        await Assert.That(endpoints).IsNotEmpty();
    }

    [Test]
    public async Task AddAsbEmulatorUi_ShouldExcludeFromManifest()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();

        // Act
        var uiResource = builder.AddAsbEmulatorUi("asb-ui", serviceBus);

        // Assert
        var manifestExclusion = uiResource.Resource.Annotations
            .OfType<ManifestPublishingCallbackAnnotation>()
            .Any();
        
        await Assert.That(manifestExclusion).IsTrue();
    }

    [Test]
    public async Task AddAsbEmulatorUi_ShouldHaveEnvironmentCallback()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();

        // Act
        var uiResource = builder.AddAsbEmulatorUi("asb-ui", serviceBus);

        // Assert
        var envCallbacks = uiResource.Resource.Annotations
            .OfType<EnvironmentCallbackAnnotation>();
        
        await Assert.That(envCallbacks).IsNotEmpty();
    }

    [Test]
    public async Task AddAsbEmulatorUi_ShouldUseCorrectDockerImage()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();

        // Act
        var uiResource = builder.AddAsbEmulatorUi("asb-ui", serviceBus);

        // Assert
        var containerAnnotation = uiResource.Resource.Annotations
            .OfType<ContainerImageAnnotation>()
            .FirstOrDefault();
        
        await Assert.That(containerAnnotation).IsNotNull();
        await Assert.That(containerAnnotation!.Image).IsEqualTo("andrewjpoole/aspireasbemulatorui");
        await Assert.That(containerAnnotation.Registry).IsEqualTo("docker.io");
    }

    [Test]
    public async Task AddAsbEmulatorUi_ShouldTargetPort8080()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();

        // Act
        var uiResource = builder.AddAsbEmulatorUi("asb-ui", serviceBus);

        // Assert
        var endpoints = uiResource.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Where(e => e.TargetPort == 8080);
        
        await Assert.That(endpoints).IsNotEmpty();
    }

    [Test]
    public async Task AddAsbEmulatorUi_ResourceType_ShouldBeAsbEmulatorUiResource()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();

        // Act
        var uiResource = builder.AddAsbEmulatorUi("asb-ui", serviceBus);

        // Assert
        var resource = uiResource.Resource;
        await Assert.That(resource is AsbEmulatorUiResource).IsTrue();
    }

    [Test]
    public async Task AddAsbEmulatorUi_WithDifferentNames_ShouldCreateUniqueResources()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();

        // Act
        var uiResource1 = builder.AddAsbEmulatorUi("asb-ui-1", serviceBus);
        var uiResource2 = builder.AddAsbEmulatorUi("asb-ui-2", serviceBus, httpPort: 8001);

        // Assert
        await Assert.That(uiResource1.Resource.Name).IsEqualTo("asb-ui-1");
        await Assert.That(uiResource2.Resource.Name).IsEqualTo("asb-ui-2");
        await Assert.That(uiResource1.Resource).IsNotEqualTo(uiResource2.Resource);
    }
}
