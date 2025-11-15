namespace AspireAsbEmulatorUi.App.Models;

public class ServiceBusEntityInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public long ActiveMessageCount { get; set; }
    public long DeadletterMessageCount { get; set; }

    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return string.Empty;
            var s = Name;
            // Remove common namespace prefix
            const string prefix = "SBEMULATORNS";
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(prefix.Length);
            }
            // Trim common separators
            s = s.TrimStart('|', '/', '\\', ':', '.', '-', '_');

            // If there's a type prefix like "QUEUE:" or "TOPIC:", remove it
            var parts = s.Split(new[] { ':', '|' }, 2);
            if (parts.Length == 2)
            {
                var first = parts[0].Trim();
                if (first.Equals("QUEUE", StringComparison.OrdinalIgnoreCase) || first.Equals("TOPIC", StringComparison.OrdinalIgnoreCase))
                {
                    s = parts[1];
                }
            }

            // Finally trim any leading separators that may remain
            s = s.TrimStart('|', '/', '\\', ':', '.', '-', '_');
            return s;
        }
    }
}
