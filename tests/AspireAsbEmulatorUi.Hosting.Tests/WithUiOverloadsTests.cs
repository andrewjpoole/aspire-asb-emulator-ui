using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.AsbEmulatorUi.Integration;
using AspireAsbEmulatorUi.Models;
using System.Text.Json;

namespace AspireAsbEmulatorUi.Hosting.Tests;

public class WithUiCannedMessagesTests
{
    [Test]
    public async Task WithCannedMessages_OnEmulator_ShouldAddEnvironmentCallbackToUiResource()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var cannedMessages = new Dictionary<string, Dictionary<string, CannedMessage>>
        {
            ["queue1"] = new()
            {
                ["scenario1"] = new CannedMessage
                {
                    ContentType = "application/json",
                    Body = "{\"test\": \"data\"}"
                }
            }
        };

        // Act
        builder.AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c.WithUi().WithCannedMessages(cannedMessages));

        // Assert
        var uiResource = builder.Resources.OfType<AsbEmulatorUiResource>().Single();
        var envCallbacks = uiResource.Annotations.OfType<EnvironmentCallbackAnnotation>();
        await Assert.That(envCallbacks).IsNotEmpty();
    }

    [Test]
    public async Task WithCannedMessages_OnEmulator_ShouldReturnEmulatorBuilder()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var cannedMessages = new Dictionary<string, Dictionary<string, CannedMessage>>
        {
            ["queue1"] = new()
            {
                ["scenario1"] = new CannedMessage { Body = "test" }
            }
        };

        // Act
        var result = builder.AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c.WithUi().WithCannedMessages(cannedMessages));

        // Assert - returns the service bus builder for continued chaining
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Resource).IsTypeOf<AzureServiceBusResource>();
    }

    [Test]
    public async Task WithCannedMessages_OnEmulator_WithoutWithUi_ShouldThrow()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var cannedMessages = new Dictionary<string, Dictionary<string, CannedMessage>>
        {
            ["queue1"] = new()
            {
                ["scenario1"] = new CannedMessage { Body = "test" }
            }
        };

        // Act & Assert
        var threwException = false;
        try
        {
            builder.AddAzureServiceBus("servicebus")
                .RunAsEmulator(c => c.WithCannedMessages(cannedMessages));
        }
        catch (InvalidOperationException ex)
        {
            threwException = true;
            await Assert.That(ex.Message).Contains("WithUi()");
        }

        await Assert.That(threwException).IsTrue();
    }

    [Test]
    public async Task WithCannedMessages_OnEmulator_ShouldBeChainableWithLifetime()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var cannedMessages = new Dictionary<string, Dictionary<string, CannedMessage>>
        {
            ["queue1"] = new()
            {
                ["scenario1"] = new CannedMessage { Body = "test" }
            }
        };

        // Act
        var result = builder.AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c
                .WithLifetime(ContainerLifetime.Persistent)
                .WithUi()
                .WithCannedMessages(cannedMessages));

        // Assert
        await Assert.That(result).IsNotNull();
        var uiResource = builder.Resources.OfType<AsbEmulatorUiResource>().Single();
        await Assert.That(uiResource).IsNotNull();
    }

    [Test]
    public async Task WithCannedMessages_OnEmulator_CalledMultipleTimes_ShouldMerge()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var messages1 = new Dictionary<string, Dictionary<string, CannedMessage>>
        {
            ["queue1"] = new()
            {
                ["scenario1"] = new CannedMessage { Body = "test1" }
            }
        };
        var messages2 = new Dictionary<string, Dictionary<string, CannedMessage>>
        {
            ["queue2"] = new()
            {
                ["scenario2"] = new CannedMessage { Body = "test2" }
            }
        };

        // Act
        builder.AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c.WithUi()
                .WithCannedMessages(messages1)
                .WithCannedMessages(messages2));

        // Assert - UI resource should have multiple environment callbacks
        var uiResource = builder.Resources.OfType<AsbEmulatorUiResource>().Single();
        var callbacks = uiResource.Annotations
            .OfType<EnvironmentCallbackAnnotation>()
            .ToList();
        await Assert.That(callbacks.Count).IsGreaterThanOrEqualTo(2);
    }
}

public class WithUiOverridenSettingsFileTests
{
    private string? _tempFile;

    [Before(Test)]
    public void Setup()
    {
        _tempFile = Path.GetTempFileName();
    }

    [After(Test)]
    public void Cleanup()
    {
        if (_tempFile != null && File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Test]
    public async Task WithOverridenSettingsFile_OnEmulator_ShouldAddEnvironmentCallbackToUiResource()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var settings = new Settings
        {
            DefaultContentType = "application/xml",
            CannedMessages = new()
            {
                ["queue1"] = new()
                {
                    ["scenario1"] = new CannedMessage { Body = "test" }
                }
            }
        };
        await File.WriteAllTextAsync(_tempFile!, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        // Act
        builder.AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c.WithUi().WithOverridenSettingsFile(_tempFile!));

        // Assert
        var uiResource = builder.Resources.OfType<AsbEmulatorUiResource>().Single();
        var envCallbacks = uiResource.Annotations.OfType<EnvironmentCallbackAnnotation>();
        await Assert.That(envCallbacks).IsNotEmpty();
    }

    [Test]
    public async Task WithOverridenSettingsFile_OnEmulator_ShouldReturnEmulatorBuilder()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var settings = new Settings();
        await File.WriteAllTextAsync(_tempFile!, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        // Act
        var result = builder.AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c.WithUi().WithOverridenSettingsFile(_tempFile!));

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Resource).IsTypeOf<AzureServiceBusResource>();
    }

    [Test]
    public async Task WithOverridenSettingsFile_OnEmulator_WithoutWithUi_ShouldThrow()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var settings = new Settings();
        await File.WriteAllTextAsync(_tempFile!, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        // Act & Assert
        var threwException = false;
        try
        {
            builder.AddAzureServiceBus("servicebus")
                .RunAsEmulator(c => c.WithOverridenSettingsFile(_tempFile!));
        }
        catch (InvalidOperationException ex)
        {
            threwException = true;
            await Assert.That(ex.Message).Contains("WithUi()");
        }

        await Assert.That(threwException).IsTrue();
    }

    [Test]
    public async Task WithOverridenSettingsFile_OnEmulator_ShouldBeChainableWithLifetime()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var settings = new Settings();
        await File.WriteAllTextAsync(_tempFile!, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        // Act
        var result = builder.AddAzureServiceBus("servicebus")
            .RunAsEmulator(c => c
                .WithLifetime(ContainerLifetime.Persistent)
                .WithUi()
                .WithOverridenSettingsFile(_tempFile!));

        // Assert
        await Assert.That(result).IsNotNull();
        var uiResource = builder.Resources.OfType<AsbEmulatorUiResource>().Single();
        await Assert.That(uiResource).IsNotNull();
    }
}
