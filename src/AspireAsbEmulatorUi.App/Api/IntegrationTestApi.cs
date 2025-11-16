using AspireAsbEmulatorUi.App.Models;
using AspireAsbEmulatorUi.App.Services;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Mvc;

namespace AspireAsbEmulatorUi.App.Api;

/// <summary>
/// API endpoints for integration testing - programmatic message sending
/// </summary>
public static class IntegrationTestApi
{
    public static void MapIntegrationTestEndpoints(this WebApplication app)
    {       
        var api = app.MapGroup("/api");

        // Send a canned message
        api.MapPost("/canned/{entity}/{scenario}", async (
            [FromRoute] string entity,
            [FromRoute] string scenario,
            [FromServices] SettingsService settingsService,
            [FromServices] ServiceBusService serviceBusService,
            [FromServices] PlaceholderService placeholderService) =>
        {
            var settings = settingsService.GetSettings();

            if (!settings.CannedMessages.TryGetValue(entity, out var scenarios))
                return Results.NotFound(new { error = $"No canned messages found for entity: {entity}" });

            if (!scenarios.TryGetValue(scenario, out var cannedMessage))
                return Results.NotFound(new { error = $"Scenario '{scenario}' not found for entity: {entity}" });

            try
            {
                // Evaluate placeholders
                var body = placeholderService.EvaluatePlaceholders(cannedMessage.Body);

                // Build the message
                var message = new ServiceBusMessage(body)
                {
                    ContentType = cannedMessage.ContentType
                };

                // Add application properties
                foreach (var prop in cannedMessage.ApplicationProperties)
                {
                    var value = prop.Value?.ToString() ?? string.Empty;
                    message.ApplicationProperties[prop.Key] = placeholderService.EvaluatePlaceholders(value);
                }

                // Add broker properties
                if (cannedMessage.BrokerProperties.TryGetValue("Subject", out var subject))
                    message.Subject = placeholderService.EvaluatePlaceholders(subject?.ToString() ?? string.Empty);

                if (cannedMessage.BrokerProperties.TryGetValue("SessionId", out var sessionId))
                    message.SessionId = placeholderService.EvaluatePlaceholders(sessionId?.ToString() ?? string.Empty);

                if (cannedMessage.BrokerProperties.TryGetValue("PartitionKey", out var partitionKey))
                    message.PartitionKey = placeholderService.EvaluatePlaceholders(partitionKey?.ToString() ?? string.Empty);

                if (cannedMessage.BrokerProperties.TryGetValue("MessageId", out var messageId))
                    message.MessageId = placeholderService.EvaluatePlaceholders(messageId?.ToString() ?? string.Empty);

                if (cannedMessage.BrokerProperties.TryGetValue("CorrelationId", out var correlationId))
                    message.CorrelationId = placeholderService.EvaluatePlaceholders(correlationId?.ToString() ?? string.Empty);

                if (cannedMessage.BrokerProperties.TryGetValue("TimeToLive", out var ttl) && ttl != null)
                {
                    if (int.TryParse(ttl.ToString(), out var ttlSeconds))
                        message.TimeToLive = TimeSpan.FromSeconds(ttlSeconds);
                }

                if (cannedMessage.BrokerProperties.TryGetValue("ScheduledEnqueueTime", out var scheduledTime) && scheduledTime != null)
                {
                    if (DateTimeOffset.TryParse(scheduledTime.ToString(), out var scheduled))
                        message.ScheduledEnqueueTime = scheduled;
                }

                // Send the message using the new overload
                await serviceBusService.SendMessageAsync(entity, message);

                return Results.Ok(new
                {
                    success = true,
                    entity,
                    scenario,
                    messageId = message.MessageId,
                    sentAt = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to send canned message");
            }
        })
        .WithName("SendCannedMessage");

        // Send a custom message
        api.MapPost("/messages/{entity}", async (
            [FromRoute] string entity,
            [FromBody] SendMessageRequest request,
            [FromServices] ServiceBusService serviceBusService,
            [FromServices] PlaceholderService placeholderService) =>
        {
            try
            {
                // Evaluate placeholders
                var body = placeholderService.EvaluatePlaceholders(request.Body ?? string.Empty);

                // Build the message
                var message = new ServiceBusMessage(body)
                {
                    ContentType = request.ContentType ?? "application/json"
                };

                // Add application properties
                if (request.ApplicationProperties != null)
                {
                    foreach (var prop in request.ApplicationProperties)
                    {
                        var value = prop.Value?.ToString() ?? string.Empty;
                        message.ApplicationProperties[prop.Key] = placeholderService.EvaluatePlaceholders(value);
                    }
                }

                // Add broker properties
                if (request.BrokerProperties != null)
                {
                    if (request.BrokerProperties.TryGetValue("Subject", out var subject))
                        message.Subject = placeholderService.EvaluatePlaceholders(subject?.ToString() ?? string.Empty);

                    if (request.BrokerProperties.TryGetValue("SessionId", out var sessionId))
                        message.SessionId = placeholderService.EvaluatePlaceholders(sessionId?.ToString() ?? string.Empty);

                    if (request.BrokerProperties.TryGetValue("PartitionKey", out var partitionKey))
                        message.PartitionKey = placeholderService.EvaluatePlaceholders(partitionKey?.ToString() ?? string.Empty);

                    if (request.BrokerProperties.TryGetValue("MessageId", out var messageId))
                        message.MessageId = placeholderService.EvaluatePlaceholders(messageId?.ToString() ?? string.Empty);

                    if (request.BrokerProperties.TryGetValue("CorrelationId", out var correlationId))
                        message.CorrelationId = placeholderService.EvaluatePlaceholders(correlationId?.ToString() ?? string.Empty);

                    if (request.BrokerProperties.TryGetValue("TimeToLive", out var ttl) && ttl != null)
                    {
                        if (int.TryParse(ttl.ToString(), out var ttlSeconds))
                            message.TimeToLive = TimeSpan.FromSeconds(ttlSeconds);
                    }

                    if (request.BrokerProperties.TryGetValue("ScheduledEnqueueTime", out var scheduledTime) && scheduledTime != null)
                    {
                        if (DateTimeOffset.TryParse(scheduledTime.ToString(), out var scheduled))
                            message.ScheduledEnqueueTime = scheduled;
                    }
                }

                // Send the message using the new overload
                await serviceBusService.SendMessageAsync(entity, message);

                return Results.Created($"/api/messages/{entity}", new
                {
                    success = true,
                    entity,
                    messageId = message.MessageId,
                    sentAt = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to send message");
            }
        })
        .WithName("SendCustomMessage");
    }
}

public record SendMessageRequest
{
    public string? ContentType { get; init; }
    public string? Body { get; init; }
    public Dictionary<string, object>? ApplicationProperties { get; init; }
    public Dictionary<string, object>? BrokerProperties { get; init; }
}
