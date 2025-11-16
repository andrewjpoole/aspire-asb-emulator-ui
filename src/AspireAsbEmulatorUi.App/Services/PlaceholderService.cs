using System.Text.RegularExpressions;

namespace AspireAsbEmulatorUi.App.Services;

public class PlaceholderService
{
    /// <summary>
    /// Processes placeholders in a string:
    /// - ~newGuid~ -> generates a new GUID
    /// - ~now~ -> current UTC time in ISO 8601 format
    /// - ~now+5m~ -> current UTC time plus 5 minutes
    /// - ~now+1h~ -> current UTC time plus 1 hour
    /// - ~now+1d~ -> current UTC time plus 1 day
    /// Supported units: s (seconds), m (minutes), h (hours), d (days)
    /// </summary>
    public string ProcessPlaceholders(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = input;
        
        // Replace ~newGuid~ placeholders (case-insensitive)
        // Each occurrence gets a unique GUID
        while (Regex.IsMatch(result, @"~newGuid~", RegexOptions.IgnoreCase))
        {
            result = Regex.Replace(result, @"~newGuid~", Guid.NewGuid().ToString(), 
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            // Break after first replacement to generate unique GUIDs per occurrence
            break;
        }
        
        // Continue replacing remaining occurrences
        result = Regex.Replace(result, @"~newGuid~", 
            match => Guid.NewGuid().ToString(), 
            RegexOptions.IgnoreCase);

        // Replace ~now~ placeholder (case-insensitive)
        result = Regex.Replace(result, @"~now~", 
            match => DateTime.UtcNow.ToString("O"), 
            RegexOptions.IgnoreCase);

        // Replace time offset placeholders like ~now+5m~, ~now+1h~, ~now+1d~ (case-insensitive)
        result = Regex.Replace(result, 
            @"~now\+(\d+)(s|m|h|d)~", 
            match => 
            {
                var amount = int.Parse(match.Groups[1].Value);
                var unit = match.Groups[2].Value.ToLower();
                var offset = unit switch
                {
                    "s" => TimeSpan.FromSeconds(amount),
                    "m" => TimeSpan.FromMinutes(amount),
                    "h" => TimeSpan.FromHours(amount),
                    "d" => TimeSpan.FromDays(amount),
                    _ => TimeSpan.Zero
                };
                return DateTime.UtcNow.Add(offset).ToString("O");
            },
            RegexOptions.IgnoreCase);
        
        // Support negative offsets like ~now-5m~
        result = Regex.Replace(result, 
            @"~now-(\d+)(s|m|h|d)~", 
            match => 
            {
                var amount = int.Parse(match.Groups[1].Value);
                var unit = match.Groups[2].Value.ToLower();
                var offset = unit switch
                {
                    "s" => TimeSpan.FromSeconds(amount),
                    "m" => TimeSpan.FromMinutes(amount),
                    "h" => TimeSpan.FromHours(amount),
                    "d" => TimeSpan.FromDays(amount),
                    _ => TimeSpan.Zero
                };
                return DateTime.UtcNow.Subtract(offset).ToString("O");
            },
            RegexOptions.IgnoreCase);

        return result;
    }

    /// <summary>
    /// Processes placeholders in a dictionary of values
    /// </summary>
    public Dictionary<string, object?> ProcessPlaceholders(Dictionary<string, object?> input)
    {
        var result = new Dictionary<string, object?>();
        foreach (var kvp in input)
        {
            var value = kvp.Value?.ToString() ?? string.Empty;
            result[kvp.Key] = ProcessPlaceholders(value);
        }
        return result;
    }

    /// <summary>
    /// Alias for ProcessPlaceholders - evaluates placeholders in a string
    /// </summary>
    public string EvaluatePlaceholders(string input) => ProcessPlaceholders(input);
}
