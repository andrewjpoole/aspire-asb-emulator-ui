using AspireAsbEmulatorUi.App.Models;
using Microsoft.Data.SqlClient;

namespace AspireAsbEmulatorUi.App.Services;

public class AsbEmulatorSqlEntityRepository
{
    private string _connectionString = string.Empty;

    public AsbEmulatorSqlEntityRepository()
    {
    }

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
        cmd.CommandText = @"SELECT 
            e.Id AS EntityId,
            e.Name AS EntityName,
            e.Type AS EntityType
        FROM [SbMessageContainerDatabase00001].[dbo].[EntityLookupTable] e
        WHERE e.Name LIKE 'SBEMULATORNS%'
            AND e.Name NOT LIKE '%$transfer'
            AND e.Name NOT LIKE '%$DEFAULT'
        ORDER BY e.Name";

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(reader.GetOrdinal("EntityId"));
            var fullName = reader.GetString(reader.GetOrdinal("EntityName"));
            var typeByte = reader.GetByte(reader.GetOrdinal("EntityType"));

            if (string.IsNullOrEmpty(fullName))
                continue;

            // Clean the name by removing the namespace prefix
            string cleanName = fullName;
            if (fullName.StartsWith("SBEMULATORNS:QUEUE:", StringComparison.OrdinalIgnoreCase))
            {
                cleanName = fullName.Substring("SBEMULATORNS:QUEUE:".Length);
            }
            else if (fullName.StartsWith("SBEMULATORNS:TOPIC:", StringComparison.OrdinalIgnoreCase))
            {
                cleanName = fullName.Substring("SBEMULATORNS:TOPIC:".Length);
            }

            // Determine entity type
            string entityType;
            string? parentTopic = null;
            
            if (typeByte == 1)
            {
                entityType = "Topic";
            }
            else if (typeByte == 2 || typeByte == 3)
            {
                entityType = "Subscription";
                // Extract parent topic from subscription name (format: TopicName|SubscriptionName)
                var pipeIndex = cleanName.IndexOf('|');
                if (pipeIndex > 0)
                {
                    parentTopic = cleanName.Substring(0, pipeIndex);
                }
            }
            else
            {
                entityType = "Queue";
            }

            results.Add(new ServiceBusEntityInfo
            {
                Id = id,
                Name = cleanName,
                EntityType = entityType,
                ParentTopic = parentTopic,
                ActiveMessageCount = 0,
                DeadletterMessageCount = 0,
            });
        }

        return results;
    }
}
