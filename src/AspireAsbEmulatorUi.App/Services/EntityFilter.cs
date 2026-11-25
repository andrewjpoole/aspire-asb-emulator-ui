using AspireAsbEmulatorUi.App.Models;

namespace AspireAsbEmulatorUi.App.Services;

internal static class EntityFilter
{
    public static List<ServiceBusEntityInfo> Apply(IReadOnlyList<ServiceBusEntityInfo> entities, string? filter)
    {
        var matches = entities.Where(entity => Matches(entity, filter)).ToHashSet();
        var parentTopics = matches
            .Where(entity => string.Equals(entity.EntityType, "Subscription", StringComparison.OrdinalIgnoreCase))
            .Select(entity => entity.ParentTopic)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return entities.Where(entity => matches.Contains(entity) ||
            (string.Equals(entity.EntityType, "Topic", StringComparison.OrdinalIgnoreCase) &&
             parentTopics.Contains(entity.Name))).ToList();
    }

    public static bool Matches(ServiceBusEntityInfo entity, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;

        if (filter.Contains('*'))
        {
            return MatchesWildcard(entity.Name, filter) || MatchesWildcard(entity.DisplayName, filter);
        }

        return entity.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            entity.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            (long.TryParse(filter, out var id) && entity.Id == id) ||
            MatchesInitials(entity.Name, filter) || MatchesInitials(entity.DisplayName, filter);
    }

    private static bool MatchesWildcard(string name, string pattern)
    {
        var nameIndex = 0;
        var patternIndex = 0;
        var lastStar = -1;
        var retryIndex = 0;

        while (nameIndex < name.Length)
        {
            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                lastStar = patternIndex++;
                retryIndex = nameIndex;
            }
            else if (patternIndex < pattern.Length && EqualIgnoreCase(name[nameIndex], pattern[patternIndex]))
            {
                nameIndex++;
                patternIndex++;
            }
            else if (lastStar >= 0)
            {
                // Retry only the most recent star, without recursive pattern expansion.
                patternIndex = lastStar + 1;
                nameIndex = ++retryIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;
        return patternIndex == pattern.Length;
    }

    private static bool MatchesInitials(string name, string filter)
    {
        var filterIndex = 0;
        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (!char.IsLetterOrDigit(current)) continue;

            var isWordStart = index == 0 || !char.IsLetterOrDigit(name[index - 1]) ||
                (char.IsUpper(current) &&
                 (char.IsLower(name[index - 1]) || char.IsDigit(name[index - 1]) ||
                  (char.IsUpper(name[index - 1]) && index + 1 < name.Length && char.IsLower(name[index + 1]))));

            if (!isWordStart) continue;
            if (!EqualIgnoreCase(current, filter[filterIndex])) return false;
            if (++filterIndex == filter.Length) return true;
        }

        return false;
    }

    private static bool EqualIgnoreCase(char left, char right) =>
        char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
}
