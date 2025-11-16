namespace AspireAsbEmulatorUi.App.Models;

public class Settings
{
    public List<string> ContentTypes { get; set; } = new()
    {
        "application/json",
        "text/plain",
        "application/xml",
        "application/octet-stream"
    };

    public string DefaultContentType { get; set; } = "application/json";

    public List<KeyValuePair<string, string>> CommonApplicationProperties { get; set; } = new()
    {
        new KeyValuePair<string, string>("MessageType", "MT_EVENT")
    };

    public Dictionary<string, Dictionary<string, CannedMessage>> CannedMessages { get; set; } = new();
}

public class CannedMessage
{
    public string ContentType { get; set; } = "application/json";
    public string Body { get; set; } = string.Empty;
    public Dictionary<string, object> BrokerProperties { get; set; } = new();
    public Dictionary<string, object> ApplicationProperties { get; set; } = new();
}
