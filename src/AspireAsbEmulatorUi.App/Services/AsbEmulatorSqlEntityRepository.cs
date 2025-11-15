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
        // Select entities whose Name starts with SBEMULATORNS and use the MessageCount column
        cmd.CommandText = @"SELECT TOP (1000) [Id]
      ,[Name]
      ,[Type]
      ,[MessageCount]
  FROM [SbMessageContainerDatabase00001].[dbo].[EntityLookupTable]
  WHERE [Name] LIKE 'SBEMULATORNS%'
  ORDER BY [Name]";

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var queues = new Dictionary<string, (long Id, long Active, long Deadletter)>(StringComparer.OrdinalIgnoreCase);
        var topics = new Dictionary<string, (long Id, long Active, long Deadletter)>(StringComparer.OrdinalIgnoreCase);
        var subscriptions = new Dictionary<string, (long Id, long Active, long Deadletter, string ParentTopic)>(StringComparer.OrdinalIgnoreCase);
        const string transferSuffix = "$TRANSFER";
        char[] separators = new[] { '|', ':', '/', '\\', '.', '-', '_' };

        while (await reader.ReadAsync(cancellationToken))
        {
            // Known schema types: Id bigint, Name nvarchar, Type tinyint, MessageCount bigint
            var id = reader.GetInt64(reader.GetOrdinal("Id"));
            var fullName = reader.GetString(reader.GetOrdinal("Name"));
            var typeByte = reader.GetByte(reader.GetOrdinal("Type"));
            var messageCount = reader.GetInt64(reader.GetOrdinal("MessageCount"));

            if (string.IsNullOrEmpty(fullName))
                continue;

            // Parse the entity name format: SBEMULATORNS:QUEUE:QUEUE-NAME or SBEMULATORNS:TOPIC:TOPIC-NAME
            // Extract the clean name by removing the prefix
            string cleanName = fullName;
            if (fullName.StartsWith("SBEMULATORNS:QUEUE:", StringComparison.OrdinalIgnoreCase))
            {
                cleanName = fullName.Substring("SBEMULATORNS:QUEUE:".Length);
            }
            else if (fullName.StartsWith("SBEMULATORNS:TOPIC:", StringComparison.OrdinalIgnoreCase))
            {
                cleanName = fullName.Substring("SBEMULATORNS:TOPIC:".Length);
            }

            // Check if this is a subscription by looking for | in the clean name (but not just transfer suffix)
            var hasSubscriptionSuffix = cleanName.Contains('|') && !cleanName.EndsWith(transferSuffix, StringComparison.OrdinalIgnoreCase);
            
            // Determine entity type
            // Type 0 = Queue or transfer entity
            // Type 1 = Topic
            // Type 2 = Subscription
            // Type 3 = Subscription default
            string entityType;
            string? parentTopic = null;
            
            if (hasSubscriptionSuffix || typeByte == 2 || typeByte == 3)
            {
                // It's a subscription entity
                entityType = "Subscription";
                
                // Extract parent topic name (everything before the first |)
                var pipeIndex = cleanName.IndexOf('|');
                if (pipeIndex > 0)
                {
                    parentTopic = cleanName.Substring(0, pipeIndex);
                }
            }
            else if (typeByte == 1 || fullName.Contains(":TOPIC:", StringComparison.OrdinalIgnoreCase))
            {
                // Type 1 or has TOPIC in the prefix
                entityType = "Topic";
            }
            else
            {
                // Type 0 and has QUEUE in the prefix
                entityType = "Queue";
            }

            // Determine canonical base name for grouping (strip transfer suffix and any separator before it)
            string baseName = cleanName;
            
            // Strip $TRANSFER suffix
            if (baseName.EndsWith(transferSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var pos = baseName.Length - transferSuffix.Length;
                if (pos > 0 && baseName[pos - 1] == '|') pos--;
                baseName = baseName.Substring(0, pos);
            }
            
            // Strip $DEFAULT suffix (subscription variant)
            const string defaultSuffix = "$DEFAULT";
            if (baseName.EndsWith(defaultSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var pos = baseName.Length - defaultSuffix.Length;
                if (pos > 0 && baseName[pos - 1] == '|') pos--;
                baseName = baseName.Substring(0, pos);
            }

            if (entityType == "Subscription")
            {
                // Handle subscriptions - group by base name, combining counts from all variants
                // Main entity: active messages
                // $DEFAULT entity: also active messages (add to active)
                // $TRANSFER entity: dead-letter messages
                
                if (cleanName.EndsWith(transferSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    // Transfer/DLQ entity - add to deadletter count
                    if (!subscriptions.TryGetValue(baseName, out var tuple)) 
                        tuple = (Id: id, Active: 0, Deadletter: 0, ParentTopic: parentTopic ?? string.Empty);
                    tuple.Deadletter += messageCount;
                    if (tuple.Id == 0) tuple.Id = id;
                    subscriptions[baseName] = tuple;
                }
                else
                {
                    // Main entity or $DEFAULT variant - add to active count
                    if (!subscriptions.TryGetValue(baseName, out var tuple)) 
                        tuple = (Id: id, Active: 0, Deadletter: 0, ParentTopic: parentTopic ?? string.Empty);
                    tuple.Active += messageCount;
                    if (tuple.Id == 0) tuple.Id = id;
                    subscriptions[baseName] = tuple;
                }
            }
            else
            {
                var map = string.Equals(entityType, "Topic", StringComparison.OrdinalIgnoreCase) ? topics : queues;

                if (cleanName.EndsWith(transferSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    // This is a transfer/DLQ entity
                    if (!map.TryGetValue(baseName, out var tuple)) tuple = (Id: id, Active: 0, Deadletter: 0);
                    tuple.Deadletter += messageCount;
                    if (tuple.Id == 0) tuple.Id = id;
                    map[baseName] = tuple;
                }
                else
                {
                    // This is the main entity
                    if (!map.TryGetValue(baseName, out var tuple)) tuple = (Id: id, Active: 0, Deadletter: 0);
                    tuple.Active += messageCount;
                    if (tuple.Id == 0) tuple.Id = id;
                    map[baseName] = tuple;
                }
            }
        }

        // Build results for queues first
        foreach (var kv in queues.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(new ServiceBusEntityInfo
            {
                Id = kv.Value.Id,
                Name = kv.Key,
                EntityType = "Queue",
                ActiveMessageCount = kv.Value.Active,
                DeadletterMessageCount = kv.Value.Deadletter,
            });
        }

        // Then topics
        foreach (var kv in topics.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(new ServiceBusEntityInfo
            {
                Id = kv.Value.Id,
                Name = kv.Key,
                EntityType = "Topic",
                ActiveMessageCount = kv.Value.Active,
                DeadletterMessageCount = kv.Value.Deadletter,
            });
        }

        // Finally subscriptions (as children of topics)
        foreach (var kv in subscriptions.OrderBy(k => k.Value.ParentTopic).ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(new ServiceBusEntityInfo
            {
                Id = kv.Value.Id,
                Name = kv.Key,
                EntityType = "Subscription",
                ParentTopic = kv.Value.ParentTopic,
                ActiveMessageCount = kv.Value.Active,
                DeadletterMessageCount = kv.Value.Deadletter,
            });
        }

        return results;
    }
}
