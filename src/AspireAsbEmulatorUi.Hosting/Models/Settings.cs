namespace AspireAsbEmulatorUi.Models;

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
