using Azure.Messaging.ServiceBus;
using System.Text;
using System.Text.Json;

namespace AspireAsbEmulatorUi.App.Services;

public class ServiceBusService : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<ServiceBusService> _logger;
    private readonly AsbEmulatorSqlEntityRepository _repo;
    private ServiceBusClient? _client;

    public ServiceBusService(string connectionString, ILogger<ServiceBusService> logger, AsbEmulatorSqlEntityRepository repo)
    {
        _connectionString = connectionString;
        _logger = logger;
        _repo = repo;
        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            var preview = _connectionString.Length > 80 ? _connectionString[..80] + "..." : _connectionString;
            _logger.LogInformation("ServiceBusService created with connection string preview: {Preview}", preview);
        }
        else
        {
            _logger.LogWarning("ServiceBusService created with empty connection string.");
        }
    }

    private ServiceBusClient Client
    {
        get
        {
            if (_client == null)
            {
                var cs = NormalizeConnectionString(_connectionString);
                _client = new ServiceBusClient(cs);
            }
            return _client;
        }
    }

    private string NormalizeConnectionString(string cs)
    {
        if (string.IsNullOrWhiteSpace(cs)) return cs;
        if (cs.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase)) return cs;
        // Treat as hostname
        var host = cs.Replace("http://", "").Replace("https://", "");
        var built = $"Endpoint=sb://{host};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
        _logger.LogInformation("Normalized Service Bus connection string to: {Preview}", built.Length > 80 ? built[..80] + "..." : built);
        return built;
    }

    private static string CleanEntityName(string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            return entityName ?? string.Empty;

        var clean = entityName;
        if (clean.StartsWith("SBEMULATORNS", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring("SBEMULATORNS".Length);
        }
        clean = clean.TrimStart(':', '|', '/', '\\', '.', '-', '_');
        var parts = clean.Split(new[] { ':', '|' }, 2);
        if (parts.Length == 2 && (parts[0].Equals("QUEUE", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("TOPIC", StringComparison.OrdinalIgnoreCase)))
        {
            clean = parts[1];
        }
        clean = clean.TrimStart(':', '|', '/', '\\', '.', '-', '_');
        return clean.ToLowerInvariant();
    }

    public async Task<List<Models.DisplayedMessage>> PeekMessagesAsync(string queueName, int maxMessages = 20)
    {
        var clean = CleanEntityName(queueName);
        _logger.LogInformation("Peeking messages from entity '{Entity}' (cleaned: '{Clean}')", queueName, clean);

        // Verify entity exists via repository
        if (!await EntityExistsViaRepoAsync(clean))
        {
            var errMsg = $"Entity '{clean}' was not found in the Service Bus namespace (from repository).";
            _logger.LogWarning(errMsg);
            throw new InvalidOperationException(errMsg);
        }

        var receiver = Client.CreateReceiver(clean, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });
        var messages = await receiver.PeekMessagesAsync(maxMessages);
        var results = new List<Models.DisplayedMessage>();
        foreach (var m in messages)
        {
            results.Add(ConvertToDisplayedMessage(m));
        }
        return results;
    }

    public async Task<List<Models.DisplayedMessage>> ReceiveMessagesAsync(string queueName, int maxMessages = 20)
    {
        var clean = CleanEntityName(queueName);
        _logger.LogInformation("Receiving messages from entity '{Entity}' (cleaned: '{Clean}')", queueName, clean);

        if (!await EntityExistsViaRepoAsync(clean))
        {
            var errMsg = $"Entity '{clean}' was not found in the Service Bus namespace (from repository).";
            _logger.LogWarning(errMsg);
            throw new InvalidOperationException(errMsg);
        }

        var receiver = Client.CreateReceiver(clean, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });
        var messages = await receiver.ReceiveMessagesAsync(maxMessages);
        var results = new List<Models.DisplayedMessage>();
        foreach (var m in messages)
        {
            results.Add(ConvertToDisplayedMessage(m));
        }
        return results;
    }

    public async Task SendMessageAsync(string queueName, string body, Dictionary<string, object>? applicationProperties = null, IDictionary<string, object>? brokerProperties = null)
    {
        var clean = CleanEntityName(queueName);
        _logger.LogInformation("Sending message to entity '{Entity}' (cleaned: '{Clean}')", queueName, clean);

        if (!await EntityExistsViaRepoAsync(clean))
        {
            var errMsg = $"Entity '{clean}' was not found in the Service Bus namespace (from repository).";
            _logger.LogWarning(errMsg);
            throw new InvalidOperationException(errMsg);
        }

        var sender = Client.CreateSender(clean);
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
            if (brokerProperties.TryGetValue("Subject", out var subject)) msg.Subject = subject?.ToString();
            if (brokerProperties.TryGetValue("To", out var to)) msg.To = to?.ToString();
            if (brokerProperties.TryGetValue("ReplyTo", out var replyTo)) msg.ReplyTo = replyTo?.ToString();
            if (brokerProperties.TryGetValue("ReplyToSessionId", out var rts)) msg.ReplyToSessionId = rts?.ToString();
            if (brokerProperties.TryGetValue("PartitionKey", out var pk)) msg.PartitionKey = pk?.ToString();

            // TimeToLive: accept seconds (numeric) or TimeSpan string
            if (brokerProperties.TryGetValue("TimeToLive", out var ttlObj) && ttlObj != null)
            {
                var ttlStr = ttlObj.ToString();
                if (double.TryParse(ttlStr, out var seconds))
                {
                    msg.TimeToLive = TimeSpan.FromSeconds(seconds);
                }
                else if (TimeSpan.TryParse(ttlStr, out var span))
                {
                    msg.TimeToLive = span;
                }
            }

            // ScheduledEnqueueTime: parse DateTimeOffset
            if (brokerProperties.TryGetValue("ScheduledEnqueueTime", out var schedObj) && schedObj != null)
            {
                var schedStr = schedObj.ToString();
                if (DateTimeOffset.TryParse(schedStr, out var dto))
                {
                    msg.ScheduledEnqueueTime = dto;
                }
                else if (DateTime.TryParse(schedStr, out var dt))
                {
                    msg.ScheduledEnqueueTime = new DateTimeOffset(dt.ToUniversalTime());
                }
            }
        }

        await sender.SendMessageAsync(msg);
    }    

    private async Task<bool> EntityExistsViaRepoAsync(string cleanedEntityNameLower)
    {
        try
        {
            var entities = await _repo.GetEntitiesAsync();
            foreach (var e in entities)
            {
                var candidate = CleanEntityName(e.Name);
                if (string.Equals(candidate, cleanedEntityNameLower, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check entity existence via repository");
            return false;
        }
    }

    private static Models.DisplayedMessage ConvertToDisplayedMessage(ServiceBusReceivedMessage m)
    {
        var dm = new Models.DisplayedMessage
        {
            SequenceNumber = m.SequenceNumber,
            MessageId = m.MessageId,
            CorrelationId = m.CorrelationId,
            SessionId = m.SessionId,
            EnqueuedTime = m.EnqueuedTime,
            ExpiresAt = m.ExpiresAt,
            ContentType = m.ContentType,
            Body = m.Body.ToArray() is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : string.Empty
        };

        try
        {
            foreach (var kv in m.ApplicationProperties)
            {
                dm.ApplicationProperties[kv.Key] = kv.Value;
            }
        }
        catch { }

        // Capture all broker properties
        dm.BrokerProperties["MessageId"] = m.MessageId;
        dm.BrokerProperties["CorrelationId"] = m.CorrelationId;
        dm.BrokerProperties["SequenceNumber"] = m.SequenceNumber;
        dm.BrokerProperties["EnqueuedTime"] = m.EnqueuedTime;
        dm.BrokerProperties["EnqueuedSequenceNumber"] = m.EnqueuedSequenceNumber;
        dm.BrokerProperties["ExpiresAt"] = m.ExpiresAt;
        dm.BrokerProperties["SessionId"] = m.SessionId;
        dm.BrokerProperties["ContentType"] = m.ContentType;
        dm.BrokerProperties["Subject"] = m.Subject;
        dm.BrokerProperties["To"] = m.To;
        dm.BrokerProperties["ReplyTo"] = m.ReplyTo;
        dm.BrokerProperties["ReplyToSessionId"] = m.ReplyToSessionId;
        dm.BrokerProperties["PartitionKey"] = m.PartitionKey;
        dm.BrokerProperties["TimeToLive"] = m.TimeToLive;
        dm.BrokerProperties["ScheduledEnqueueTime"] = m.ScheduledEnqueueTime;
        dm.BrokerProperties["DeliveryCount"] = m.DeliveryCount;
        dm.BrokerProperties["LockToken"] = m.LockToken;
        dm.BrokerProperties["LockedUntil"] = m.LockedUntil;
        dm.BrokerProperties["State"] = m.State.ToString();
        dm.BrokerProperties["DeadLetterErrorDescription"] = m.DeadLetterErrorDescription;
        dm.BrokerProperties["DeadLetterReason"] = m.DeadLetterReason;
        dm.BrokerProperties["DeadLetterSource"] = m.DeadLetterSource;

        return dm;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
        }
    }
}
