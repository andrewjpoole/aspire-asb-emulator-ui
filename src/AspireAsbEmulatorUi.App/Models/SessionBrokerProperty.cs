using AspireAsbEmulatorUi.App.Components;

namespace AspireAsbEmulatorUi.App.Models;

internal static class SessionBrokerProperty
{
    internal static bool IsSessionKey(string? key) =>
        string.Equals(key, "SessionId", StringComparison.OrdinalIgnoreCase);

    internal static bool IsEditing(IEnumerable<BrokerPropertiesEditor.PropertyItem> properties) =>
        properties.Any(p => p.IsEditing && (IsSessionKey(p.Key) || IsSessionKey(p.OriginalKey)));

    internal static string? GetValidationError(IReadOnlyCollection<BrokerPropertiesEditor.PropertyItem> properties, bool? requiresSession)
    {
        if (IsEditing(properties)) return "Save or cancel the SessionId broker-property edit before sending.";
        if (properties.Count(p => IsSessionKey(p.Key)) > 1) return "Duplicate SessionId broker properties. Keep one session ID.";
        if (requiresSession == true && string.IsNullOrWhiteSpace(GetValue(properties)))
            return "This queue requires a Session ID. Set SessionId in Broker properties.";
        return null;
    }

    internal static string GetValue(IEnumerable<BrokerPropertiesEditor.PropertyItem> properties) =>
        properties.FirstOrDefault(p => IsSessionKey(p.Key))?.Value ?? string.Empty;

    internal static int FindOrAddRow(List<BrokerPropertiesEditor.PropertyItem> properties)
    {
        var index = properties.FindIndex(p => p.IsEditing && (IsSessionKey(p.Key) || IsSessionKey(p.OriginalKey)));
        if (index < 0) index = properties.FindIndex(p => IsSessionKey(p.Key));
        if (index >= 0) return index;
        properties.Add(new() { Key = "SessionId", IsEditing = true });
        return properties.Count - 1;
    }
}
