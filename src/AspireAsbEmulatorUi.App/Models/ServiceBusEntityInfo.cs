namespace AspireAsbEmulatorUi.App.Models;

public class ServiceBusEntityInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? ParentTopic { get; set; }
    public long ActiveMessageCount { get; set; }
    public long DeadletterMessageCount { get; set; }

    public string DisplayName
    {
        get
        {
            // For subscriptions, show just the subscription name (after the pipe)
            if (EntityType == "Subscription" && !string.IsNullOrEmpty(ParentTopic) && Name.Contains('|'))
            {
                var parts = Name.Split('|', 2);
                if (parts.Length == 2)
                {
                    return parts[1];
                }
            }

            // For queues and topics, Name is already clean
            return Name;
        }
    }
}
