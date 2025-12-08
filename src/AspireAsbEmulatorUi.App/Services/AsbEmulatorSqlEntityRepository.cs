using AspireAsbEmulatorUi.App.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace AspireAsbEmulatorUi.App.Services;

public class AsbEmulatorSqlEntityRepository
{
    private string _connectionString = string.Empty;
    private readonly ILogger<AsbEmulatorSqlEntityRepository> _logger;

    // Parameterless constructor for test frameworks that create proxies/substitutes
    public AsbEmulatorSqlEntityRepository()
        : this(NullLogger<AsbEmulatorSqlEntityRepository>.Instance)
    {
    }

    public AsbEmulatorSqlEntityRepository(ILogger<AsbEmulatorSqlEntityRepository> logger)
    {
        _logger = logger;
    }

    public void SetConnectionString(string connectionString)
    {
        _connectionString = connectionString;
        _logger.LogInformation("Set connection string for {Repository} {connectionString}", nameof(AsbEmulatorSqlEntityRepository), connectionString);
    }

    public virtual async Task<List<ServiceBusEntityInfo>> GetEntitiesAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ServiceBusEntityInfo>();
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger.LogInformation("Asb repository connection string not set; returning 0 entities.");
            return results;
        }

        using var conn = new SqlConnection(_connectionString);
        
        try
        {
            _logger.LogInformation("Opening SQL connection to retrieve ASB emulator entities.");
            await conn.OpenAsync(cancellationToken);
        }
        catch(Exception ex)
        {
            _logger.LogError(ex, "Error opening SQL connection {errorMessage}", ex.Message);
        }

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
       _logger.LogInformation("Executing entity lookup SQL to read entity lookup table.");
       _logger.LogDebug("Entity lookup SQL: {Sql}", cmd.CommandText);
      
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var rows = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(reader.GetOrdinal("EntityId"));
            var fullName = reader.GetString(reader.GetOrdinal("EntityName"));
            var typeByte = reader.GetByte(reader.GetOrdinal("EntityType"));

            if (string.IsNullOrEmpty(fullName))
                continue;

            // Clean the name by removing the namespace prefix
            string cleanName = StripNamespacePrefix(fullName);
            rows++;
            _logger.LogInformation("Entity row {Row}: Id={Id}, FullName={FullName}, CleanName={CleanName}, Type={TypeByte}", rows, id, fullName, cleanName, typeByte);
            
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

        _logger.LogInformation("Entity lookup complete. Found {Count} rows, returning {ResultCount} entities.", rows, results.Count);
        return results;
    }

    internal static string StripNamespacePrefix(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
            return fullName ?? string.Empty;

        if (fullName.StartsWith("SBEMULATORNS:QUEUE:", StringComparison.OrdinalIgnoreCase))
        {
            return fullName.Substring("SBEMULATORNS:QUEUE:".Length);
        }
        else if (fullName.StartsWith("SBEMULATORNS:TOPIC:", StringComparison.OrdinalIgnoreCase))
        {
            return fullName.Substring("SBEMULATORNS:TOPIC:".Length);
        }

        return fullName;
    }
}
