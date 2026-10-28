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
    [Arguments(true)]
    [Arguments(false)]
    public async Task WireUpToAsbEmulator_ExposesManagementEndpointOnlyWhenAvailable(bool hasManagementEndpoint)
    {
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();
        var sql = builder.Resources.Single(r => r.Name == "servicebus-mssql");
        var sqlEndpoint = sql.Annotations.OfType<EndpointAnnotation>().First();
        sqlEndpoint.AllocatedEndpoint = new AllocatedEndpoint(sqlEndpoint, "localhost", 14330);
        sql.Annotations.Add(new ResourceUrlAnnotation { Url = "tcp://localhost:14330", Endpoint = new EndpointReference((IResourceWithEndpoints)sql, sqlEndpoint.Name) });
        var management = serviceBus.Resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "emulatorhealth");
        await Assert.That(management.TargetPort).IsEqualTo(5300);
        if (!hasManagementEndpoint) serviceBus.Resource.Annotations.Remove(management);
        var variables = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), variables, CancellationToken.None);

        await AsbEmulatorUiResourceExtensions.WireUpToAsbEmulator(context, serviceBus);

        await Assert.That(variables.ContainsKey("asb-management-endpoint")).IsEqualTo(hasManagementEndpoint);
        if (hasManagementEndpoint)
        {
            await Assert.That(variables["asb-management-endpoint"] is EndpointReference).IsTrue();
            var endpoint = (EndpointReference)variables["asb-management-endpoint"];
            await Assert.That(endpoint.EndpointName).IsEqualTo("emulatorhealth");
        }
    }

    [Test]
    public async Task WireUpToAsbEmulator_PublishMode_DoesNotExposeManagementEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();
        var variables = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish), variables, CancellationToken.None);

        await AsbEmulatorUiResourceExtensions.WireUpToAsbEmulator(context, serviceBus);

        await Assert.That(variables).IsEmpty();
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
    public async Task AddAsbEmulatorUi_WithDefaultPort_ShouldAllowAspireToAssignDynamicPort()
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
            .ToList();

        await Assert.That(endpoints.Any(e => e.Port is null)).IsTrue();
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
