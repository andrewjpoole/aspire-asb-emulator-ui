namespace AspireAsbEmulatorUi.Models;

public class CannedMessage
{
    public string ContentType { get; set; } = "application/json";
    public string Body { get; set; } = string.Empty;
    public Dictionary<string, object> BrokerProperties { get; set; } = new();
    public Dictionary<string, object> ApplicationProperties { get; set; } = new();
}
