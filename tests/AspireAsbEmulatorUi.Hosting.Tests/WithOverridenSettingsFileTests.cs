using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireAsbEmulatorUi.App.Models;
using AspireAsbEmulatorUi.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AspireAsbEmulatorUi.Hosting.Tests;

public class WithOverridenSettingsFileTests
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
    public async Task WithOverridenSettingsFile_WithValidFile_ShouldLoadSettings()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = builder.AddContainer("test-project", "test-image");
        
        var settings = new Settings
        {
            DefaultContentType = "application/xml",
            ContentTypes = ["application/xml", "text/plain"],
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
        projectResource.WithOverridenSettingsFile(_tempFile!);

        // Assert
        await Assert.That(projectResource).IsNotNull();
        
        var envCallbacks = projectResource.Resource.Annotations
            .OfType<EnvironmentCallbackAnnotation>();
        
        await Assert.That(envCallbacks).IsNotEmpty();
    }

    [Test]
    public async Task WithOverridenSettingsFile_WithNonExistentFile_ShouldNotThrow()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = builder.AddContainer("test-project", "test-image");
        
        var nonExistentFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".json");

        // Act
        projectResource.WithOverridenSettingsFile(nonExistentFile);

        // Assert - Should not throw during setup
        await Assert.That(projectResource).IsNotNull();
    }

    [Test]
    public async Task WithOverridenSettingsFile_WithInvalidJson_ShouldNotThrowDuringSetup()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = builder.AddContainer("test-project", "test-image");
        
        await File.WriteAllTextAsync(_tempFile!, "{ invalid json content }");

        // Act
        projectResource.WithOverridenSettingsFile(_tempFile!);

        // Assert - Should not throw during setup, error happens during execution
        await Assert.That(projectResource).IsNotNull();
    }

    [Test]
    public async Task WithOverridenSettingsFile_WithEmptyString_ShouldNotThrow()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = builder.AddContainer("test-project", "test-image");

        // Act
        projectResource.WithOverridenSettingsFile(string.Empty);

        // Assert
        await Assert.That(projectResource).IsNotNull();
    }

    [Test]
    public async Task WithOverridenSettingsFile_WithNullString_ShouldNotThrow()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = builder.AddContainer("test-project", "test-image");

        // Act
        projectResource.WithOverridenSettingsFile(null!);

        // Assert
        await Assert.That(projectResource).IsNotNull();
    }

    [Test]
    public async Task WithOverridenSettingsFile_WithValidComplexSettings_ShouldLoadAllProperties()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = builder.AddContainer("test-project", "test-image");
        
        var settings = new Settings
        {
            DefaultContentType = "application/xml",
            ContentTypes = ["application/xml", "text/plain", "application/json"],
            CommonApplicationProperties =
            [
                new KeyValuePair<string, string>("Prop1", "Value1"),
                new KeyValuePair<string, string>("Prop2", "Value2")
            ],
            CannedMessages = new()
            {
                ["queue1"] = new()
                {
                    ["scenario1"] = new CannedMessage
                    {
                        ContentType = "application/json",
                        Body = "{\"test\": \"data\"}",
                        ApplicationProperties = new() { ["key"] = "value" },
                        BrokerProperties = new() { ["MessageId"] = "123" }
                    }
                },
                ["topic1"] = new()
                {
                    ["scenario2"] = new CannedMessage
                    {
                        ContentType = "text/plain",
                        Body = "plain text message"
                    }
                }
            }
        };

        await File.WriteAllTextAsync(_tempFile!, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        // Act
        projectResource.WithOverridenSettingsFile(_tempFile!);

        // Assert
        await Assert.That(projectResource).IsNotNull();
        
        // Execute the callback to verify settings load correctly
        var callbacks = projectResource.Resource.Annotations
            .OfType<EnvironmentCallbackAnnotation>()
            .ToList();
        
        var envVars = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            envVars,
            CancellationToken.None
        );

        foreach (var callback in callbacks)
        {
            await callback.Callback(context);
        }

        // Verify settings were loaded
        await Assert.That(envVars.ContainsKey("AsbEmulatorUi__SettingsOverride")).IsTrue();
        
        var loadedJson = envVars["AsbEmulatorUi__SettingsOverride"]?.ToString();
        var loadedSettings = JsonSerializer.Deserialize<Settings>(loadedJson!,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        
        await Assert.That(loadedSettings).IsNotNull();
        await Assert.That(loadedSettings!.DefaultContentType).IsEqualTo("application/xml");
        await Assert.That(loadedSettings.CannedMessages).ContainsKey("queue1");
        await Assert.That(loadedSettings.CannedMessages).ContainsKey("topic1");
    }

    [Test]
    public async Task WithOverridenSettingsFile_InPublishMode_ShouldSkip()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = builder.AddContainer("test-project", "test-image");
        
        var settings = new Settings();
        await File.WriteAllTextAsync(_tempFile!, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        // Act
        projectResource.WithOverridenSettingsFile(_tempFile!);

        // Assert
        var envCallbacks = projectResource.Resource.Annotations
            .OfType<EnvironmentCallbackAnnotation>()
            .ToList();
        
        await Assert.That(envCallbacks).IsNotEmpty();
        
        // Verify that in publish mode, it returns early
        var envVars = new Dictionary<string, object>();
        var publishContext = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            envVars,
            CancellationToken.None
        );
        
        foreach (var callback in envCallbacks)
        {
            await callback.Callback(publishContext);
        }
        
        // Should not have added the override in publish mode
        await Assert.That(envVars.ContainsKey("AsbEmulatorUi__SettingsOverride")).IsFalse();
    }

    [Test]
    public async Task WithOverridenSettingsFile_AfterWithCannedMessages_ShouldThrow()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = builder.AddContainer("test-project", "test-image");
        
        var settings = new Settings();
        await File.WriteAllTextAsync(_tempFile!, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var messages = new Dictionary<string, Dictionary<string, CannedMessage>>
        {
            ["queue1"] = new() { ["scenario1"] = new CannedMessage() }
        };

        projectResource.WithCannedMessages(messages);
        projectResource.WithOverridenSettingsFile(_tempFile!);

        // Act & Assert - The exception is thrown during callback execution
        var callbacks = projectResource.Resource.Annotations
            .OfType<EnvironmentCallbackAnnotation>()
            .ToList();
        
        var envVars = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            envVars,
            CancellationToken.None
        );

        // First callback (WithCannedMessages) should succeed
        await callbacks[0].Callback(context);
        
        // Second callback (WithOverridenSettingsFile) should throw
        var threwException = false;
        try
        {
            await callbacks[1].Callback(context);
        }
        catch (InvalidOperationException)
        {
            threwException = true;
        }
        
        await Assert.That(threwException).IsTrue();
    }
}
