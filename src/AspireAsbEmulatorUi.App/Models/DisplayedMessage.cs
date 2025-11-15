namespace AspireAsbEmulatorUi.App.Models;

public class DisplayedMessage
{
    public long SequenceNumber { get; set; }
    public string? MessageId { get; set; }
    public string? CorrelationId { get; set; }
    public string? SessionId { get; set; }
    public DateTimeOffset? EnqueuedTime { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? ContentType { get; set; }
    public string Body { get; set; } = string.Empty;
    public Dictionary<string, object?> ApplicationProperties { get; set; } = new();
    public Dictionary<string, object?> BrokerProperties { get; set; } = new();
}
