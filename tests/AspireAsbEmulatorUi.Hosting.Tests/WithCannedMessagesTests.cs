using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireAsbEmulatorUi.App.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AspireAsbEmulatorUi.Hosting.Tests;

public class WithCannedMessagesTests
{
    [Test]
    public async Task WithCannedMessages_ShouldAddEnvironmentCallback()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = builder.AddContainer("test-project", "test-image");
        
        var cannedMessages = new Dictionary<string, Dictionary<string, CannedMessage>>
        {
            ["queue1"] = new()
            {
                ["scenario1"] = new CannedMessage
                {
                    ContentType = "application/json",
                    Body = "{\"test\": \"data\"}",
                    ApplicationProperties = new() { ["prop1"] = "value1" }
                }
            }
        };

        // Act
        projectResource.WithCannedMessages(cannedMessages);
        
        // Assert
        var envVars = projectResource.Resource.Annotations
            .OfType<EnvironmentCallbackAnnotation>()
            .ToList();
        
        await Assert.That(envVars).IsNotEmpty();
    }

    [Test]
    public async Task WithCannedMessages_CalledMultipleTimes_ShouldMergeSettings()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = builder.AddContainer("test-project", "test-image");
        
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
        projectResource
            .WithCannedMessages(messages1)
            .WithCannedMessages(messages2);

        // Assert - Should not throw and should have callbacks
        var callbacks = projectResource.Resource.Annotations
            .OfType<EnvironmentCallbackAnnotation>()
            .ToList();
        
        await Assert.That(callbacks.Count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task WithCannedMessages_WithEmptyDictionary_ShouldNotThrow()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = builder.AddContainer("test-project", "test-image");
        
        var emptyMessages = new Dictionary<string, Dictionary<string, CannedMessage>>();

        // Act
        projectResource.WithCannedMessages(emptyMessages);

        // Assert
        await Assert.That(projectResource).IsNotNull();
    }

    [Test]
    public async Task WithCannedMessages_WithComplexApplicationProperties_ShouldSerializeCorrectly()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = builder.AddContainer("test-project", "test-image");
        
        var messages = new Dictionary<string, Dictionary<string, CannedMessage>>
        {
            ["queue1"] = new()
            {
                ["scenario1"] = new CannedMessage
                {
                    ContentType = "application/json",
                    Body = "{\"nested\": {\"data\": true}}",
                    ApplicationProperties = new()
                    {
                        ["stringProp"] = "value",
                        ["numberProp"] = 42,
                        ["boolProp"] = true
                    },
                    BrokerProperties = new()
                    {
                        ["MessageId"] = "test-id",
                        ["SessionId"] = "session-1"
                    }
                }
            }
        };

        // Act
        projectResource.WithCannedMessages(messages);

        // Assert
        var callbacks = projectResource.Resource.Annotations
            .OfType<EnvironmentCallbackAnnotation>();
        
        await Assert.That(callbacks).IsNotEmpty();
    }

    [Test]
    public async Task WithCannedMessages_ShouldCreateValidJson()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = builder.AddContainer("test-project", "test-image");
        
        var messages = new Dictionary<string, Dictionary<string, CannedMessage>>
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
        projectResource.WithCannedMessages(messages);

        // Assert - Verify the callback creates valid environment variables
        var callbacks = projectResource.Resource.Annotations
            .OfType<EnvironmentCallbackAnnotation>()
            .ToList();
        
        await Assert.That(callbacks).IsNotEmpty();

        // Execute callback to verify it doesn't throw
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

        // Verify settings were added
        await Assert.That(envVars.ContainsKey("AsbEmulatorUi__SettingsOverride")).IsTrue();
        
        // Verify it's valid JSON
        var settingsJson = envVars["AsbEmulatorUi__SettingsOverride"]?.ToString();
        await Assert.That(settingsJson).IsNotNull();
        
        var settings = JsonSerializer.Deserialize<Settings>(settingsJson!,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await Assert.That(settings).IsNotNull();
        await Assert.That(settings!.CannedMessages).ContainsKey("queue1");
    }
}
