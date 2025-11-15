using Azure.Messaging.ServiceBus;
using System.Text;
using System.Text.Json;

namespace AspireAsbEmulatorUi.App.Services;

public class ServiceBusService : IAsyncDisposable
{
    private readonly string _connectionString;
    private ServiceBusClient? _client;

    public ServiceBusService(string connectionString)
    {
        _connectionString = connectionString;
    }

    private ServiceBusClient Client
    {
        get
        {
            if (_client == null)
            {
                // Ensure we have a valid AMQP connection string for the emulator
                var cs = _connectionString;
                
                // If the connection string doesn't start with "Endpoint=", it might be just a hostname
                // The emulator typically uses: Endpoint=sb://localhost:5672;...
                if (!string.IsNullOrWhiteSpace(cs) && !cs.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase))
                {
                    // Assume it's a hostname/endpoint and construct a proper connection string
                    // Remove any scheme prefix if present
                    cs = cs.Replace("http://", "").Replace("https://", "");
                    
                    // Build emulator connection string
                    cs = $"Endpoint=sb://{cs};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
                }
                
                _client = new ServiceBusClient(cs);
            }
            return _client;
        }
    }

    public async Task<List<string>> PeekMessagesAsync(string queueName, int maxMessages = 20)
    {
        // Strip the namespace prefix if present (e.g., SBEMULATORNS:QUEUE:myqueue -> myqueue)
        var cleanQueueName = CleanEntityName(queueName);
        var receiver = Client.CreateReceiver(cleanQueueName, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });
        var messages = await receiver.PeekMessagesAsync(maxMessages);
        var results = new List<string>();
        foreach (var m in messages)
        {
            results.Add(FormatMessage(m));
        }
        return results;
    }

    public async Task<List<string>> ReceiveMessagesAsync(string queueName, int maxMessages = 20)
    {
        // Strip the namespace prefix if present
        var cleanQueueName = CleanEntityName(queueName);
        var receiver = Client.CreateReceiver(cleanQueueName, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });
        var messages = await receiver.ReceiveMessagesAsync(maxMessages);
        var results = new List<string>();
        foreach (var m in messages)
        {
            results.Add(FormatMessage(m));
        }
        return results;
    }

    public async Task SendMessageAsync(string queueName, string body, Dictionary<string, object>? applicationProperties = null, IDictionary<string, object>? brokerProperties = null)
    {
        // Strip the namespace prefix if present (e.g., SBEMULATORNS:QUEUE:myqueue -> myqueue)
        var cleanQueueName = CleanEntityName(queueName);
        
        var sender = Client.CreateSender(cleanQueueName);
        var msg = new ServiceBusMessage(Encoding.UTF8.GetBytes(body ?? string.Empty))
        {
            ContentType = "application/json"
        };
        
        if (applicationProperties != null)
        {
            foreach (var kv in applicationProperties)
                msg.ApplicationProperties[kv.Key] = kv.Value;
        }

        if (brokerProperties != null)
        {
            if (brokerProperties.TryGetValue("SessionId", out var session)) msg.SessionId = session?.ToString();
            if (brokerProperties.TryGetValue("MessageId", out var mid)) msg.MessageId = mid?.ToString();
            if (brokerProperties.TryGetValue("CorrelationId", out var cid)) msg.CorrelationId = cid?.ToString();
            if (brokerProperties.TryGetValue("ContentType", out var ct)) msg.ContentType = ct?.ToString();
            // add more mappings as needed
        }

        await sender.SendMessageAsync(msg);
    }

    private static string CleanEntityName(string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            return entityName;

        // Remove SBEMULATORNS prefix and type markers
        // e.g., "SBEMULATORNS:QUEUE:myqueue" -> "myqueue"
        var clean = entityName;
        
        if (clean.StartsWith("SBEMULATORNS", StringComparison.OrdinalIgnoreCase))
        {
            // Remove the prefix
            clean = clean.Substring("SBEMULATORNS".Length);
        }
        
        // Remove leading separators and type prefixes
        clean = clean.TrimStart(':', '|', '/', '\\', '.', '-', '_');
        
        // If there's still a TYPE: prefix (like QUEUE: or TOPIC:), remove it
        var parts = clean.Split(new[] { ':', '|' }, 2);
        if (parts.Length == 2 && (parts[0].Equals("QUEUE", StringComparison.OrdinalIgnoreCase) || 
                                   parts[0].Equals("TOPIC", StringComparison.OrdinalIgnoreCase)))
        {
            clean = parts[1];
        }
        
        return clean;
    }

    private static string FormatMessage(ServiceBusReceivedMessage m)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"MessageId: {m.MessageId}");
        sb.AppendLine($"SequenceNumber: {m.SequenceNumber}");
        sb.AppendLine($"EnqueuedTime: {m.EnqueuedTime}");
        sb.AppendLine($"ExpiresAt: {m.ExpiresAt}");
        sb.AppendLine($"ContentType: {m.ContentType}");
        sb.AppendLine("--- Broker Properties ---");
        sb.AppendLine($"CorrelationId: {m.CorrelationId}");
        sb.AppendLine($"SessionId: {m.SessionId}");
        sb.AppendLine("--- Application Properties ---");
        try
        {
            foreach (var kv in m.ApplicationProperties)
            {
                sb.AppendLine($"{kv.Key}: {JsonSerializer.Serialize(kv.Value)}");
            }
        }
        catch { }
        sb.AppendLine("--- Body ---");
        try
        {
            var body = m.Body.ToArray();
            var text = Encoding.UTF8.GetString(body);
            sb.AppendLine(text);
        }
        catch { }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
        }
    }
}
