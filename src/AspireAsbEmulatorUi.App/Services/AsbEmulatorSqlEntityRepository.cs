using AspireAsbEmulatorUi.App.Models;
using Microsoft.Data.SqlClient;

namespace AspireAsbEmulatorUi.App.Services;

public class AsbEmulatorSqlEntityRepository
{
    private string _connectionString = string.Empty;

    public void SetConnectionString(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<ServiceBusEntityInfo>> GetEntitiesAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ServiceBusEntityInfo>();
        if (string.IsNullOrWhiteSpace(_connectionString))
            return results;

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT TOP (1000) 
        [EntityGroupId]
        ,[Id]
        ,[Name]
        ,[Type]
        ,[MessageCount]
        FROM [SbMessageContainerDatabase00001].[dbo].[EntityLookupTable]
        WHERE [Name] LIKE 'SBEMULATORNS%'
        ORDER BY [EntityGroupId], [Type], [Name]";

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        // Group entities by EntityGroupId
        var entityGroups = new Dictionary<Guid, List<(long Id, string Name, byte Type, long MessageCount)>>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var entityGroupId = reader.GetGuid(reader.GetOrdinal("EntityGroupId"));
            var id = reader.GetInt64(reader.GetOrdinal("Id"));
            var fullName = reader.GetString(reader.GetOrdinal("Name"));
            var typeByte = reader.GetByte(reader.GetOrdinal("Type"));
            var messageCount = reader.GetInt64(reader.GetOrdinal("MessageCount"));

            if (string.IsNullOrEmpty(fullName))
                continue;

            if (!entityGroups.ContainsKey(entityGroupId))
                entityGroups[entityGroupId] = new List<(long, string, byte, long)>();

            entityGroups[entityGroupId].Add((id, fullName, typeByte, messageCount));
        }

        // Process each entity group
        foreach (var group in entityGroups.OrderBy(g => g.Value.First().Name))
        {
            ProcessEntityGroup(group.Key, group.Value, results);
        }

        return results;
    }

    private void ProcessEntityGroup(Guid entityGroupId, List<(long Id, string Name, byte Type, long MessageCount)> entities, List<ServiceBusEntityInfo> results)
    {
        // Group by Type 1 (Topics), Type 0 (Queues), and Type 2/3 (Subscriptions)
        // Note: Subscriptions (Type 2/3) share EntityGroupId with their parent Topic (Type 1)
        
        // Find main Queue or Topic (Type 0 without | or Type 1)
        var mainQueueOrTopic = entities.FirstOrDefault(e => e.Type == 1 || (e.Type == 0 && !e.Name.Contains('|')));
        
        if (mainQueueOrTopic != default)
        {
            // Process Queue or Topic
            var fullName = mainQueueOrTopic.Name;
            var typeByte = mainQueueOrTopic.Type;

            string cleanName = fullName;
            if (fullName.StartsWith("SBEMULATORNS:QUEUE:", StringComparison.OrdinalIgnoreCase))
            {
                cleanName = fullName.Substring("SBEMULATORNS:QUEUE:".Length);
            }
            else if (fullName.StartsWith("SBEMULATORNS:TOPIC:", StringComparison.OrdinalIgnoreCase))
            {
                cleanName = fullName.Substring("SBEMULATORNS:TOPIC:".Length);
            }

            string entityType = typeByte == 1 ? "Topic" : "Queue";
            string baseName = cleanName;

            // Calculate message counts
            long activeCount = 0;
            long deadletterCount = 0;

            foreach (var entity in entities.Where(e => e.Type == typeByte || (e.Type == 0 && e.Name.Contains(cleanName))))
            {
                if (entity.Name.Contains("$TRANSFER", StringComparison.OrdinalIgnoreCase))
                {
                    deadletterCount += entity.MessageCount;
                }
                else
                {
                    activeCount += entity.MessageCount;
                }
            }

            Console.WriteLine($"Adding {entityType}: {baseName} (Active: {activeCount}, DLQ: {deadletterCount}, Parent: N/A)");

            results.Add(new ServiceBusEntityInfo
            {
                Id = mainQueueOrTopic.Id,
                Name = baseName,
                EntityType = entityType,
                ParentTopic = null,
                ActiveMessageCount = activeCount,
                DeadletterMessageCount = deadletterCount,
            });
        }

        // Now process subscriptions (Type 2 and 3) in this group
        var subscriptionGroups = entities
            .Where(e => e.Type == 2 || e.Type == 3 || (e.Type == 0 && e.Name.Count(c => c == '|') >= 2))
            .GroupBy(e =>
            {
                // Extract subscription base name (everything before |$TRANSFER or |$DEFAULT)
                var name = e.Name;
                if (name.StartsWith("SBEMULATORNS:TOPIC:", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring("SBEMULATORNS:TOPIC:".Length);
                }
                
                // Remove $TRANSFER suffix
                if (name.EndsWith("|$TRANSFER", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - "|$TRANSFER".Length);
                }
                
                // Remove $DEFAULT suffix
                if (name.EndsWith("|$DEFAULT", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - "|$DEFAULT".Length);
                }
                
                return name;
            })
            .Where(g => !string.IsNullOrEmpty(g.Key));

        foreach (var subGroup in subscriptionGroups)
        {
            var baseName = subGroup.Key;
            var mainSub = subGroup.FirstOrDefault(e => e.Type == 2);
            
            if (mainSub == default)
                continue;

            // Extract parent topic
            var pipeIndex = baseName.IndexOf('|');
            string? parentTopic = pipeIndex > 0 ? baseName.Substring(0, pipeIndex) : null;

            // Calculate message counts
            long activeCount = 0;
            long deadletterCount = 0;

            foreach (var entity in subGroup)
            {
                if (entity.Name.Contains("$TRANSFER", StringComparison.OrdinalIgnoreCase))
                {
                    deadletterCount += entity.MessageCount;
                }
                else
                {
                    activeCount += entity.MessageCount;
                }
            }

            Console.WriteLine($"Adding Subscription: {baseName} (Active: {activeCount}, DLQ: {deadletterCount}, Parent: {parentTopic ?? "N/A"})");

            results.Add(new ServiceBusEntityInfo
            {
                Id = mainSub.Id,
                Name = baseName,
                EntityType = "Subscription",
                ParentTopic = parentTopic,
                ActiveMessageCount = activeCount,
                DeadletterMessageCount = deadletterCount,
            });
        }
    }
}
