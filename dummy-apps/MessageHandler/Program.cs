using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Register the background service
builder.Services.AddHostedService<MessageProcessor>();

// Add Service Bus client
builder.Services.AddSingleton(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("myservicebus") 
        ?? throw new InvalidOperationException("Service Bus connection string not found");
    return new ServiceBusClient(connectionString);
});

var host = builder.Build();
await host.RunAsync();

class MessageProcessor : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly ILogger<MessageProcessor> _logger;
    private ServiceBusProcessor? _processor;

    public MessageProcessor(ServiceBusClient client, ILogger<MessageProcessor> logger)
    {
        _client = client;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Message Handler starting up...");

        // Create processor for queue-one
        _processor = _client.CreateProcessor("queue-one", new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 1,
            AutoCompleteMessages = false
        });

        // Configure message and error handlers
        _processor.ProcessMessageAsync += MessageHandler;
        _processor.ProcessErrorAsync += ErrorHandler;

        // Start processing
        await _processor.StartProcessingAsync(stoppingToken);
        _logger.LogInformation("Message Handler is now listening to queue-one");

        // Keep running until cancellation is requested
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task MessageHandler(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        _logger.LogInformation("Received message: SequenceNumber={SequenceNumber}, MessageId={MessageId}", 
            message.SequenceNumber, message.MessageId);

        try
        {
            // simulate some processing delay
            await Task.Delay(Random.Shared.Next(500, 5000));

            // Check for the "DeadLetter" application property
            if (message.ApplicationProperties.TryGetValue("DeadLetter", out var deadLetterValue) &&
                (deadLetterValue is bool shouldDeadLetter && shouldDeadLetter || 
                 deadLetterValue?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true))
            {
                _logger.LogWarning("DeadLetter property found on message {MessageId}. Dead-lettering...", message.MessageId);
                await args.DeadLetterMessageAsync(message, "DeadLetterPropertySet", "Message contained DeadLetter=true property");
            }
            else
            {
                // Process the message successfully
                var body = message.Body.ToString();
                _logger.LogInformation("Processing message {MessageId}. Body: {Body}", message.MessageId, body);
                
                // Complete the message
                await args.CompleteMessageAsync(message);
                _logger.LogInformation("Successfully completed message {MessageId}", message.MessageId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message {MessageId}", message.MessageId);
            // Let it retry or move to DLQ based on queue configuration
            await args.AbandonMessageAsync(message);
        }
    }

    private Task ErrorHandler(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Error in message processor. Source: {ErrorSource}, Entity: {EntityPath}", 
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Message Handler shutting down...");
        if (_processor != null)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
        }
        await base.StopAsync(cancellationToken);
    }
}
