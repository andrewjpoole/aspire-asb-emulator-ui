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
        const string transferSuffix = "$transfer";
        char[] separators = new[] { '|', ':', '/', '\\', '.', '-', '_' };

        while (await reader.ReadAsync(cancellationToken))
        {
            // Known schema types: Id bigint, Name nvarchar, Type tinyint, MessageCount bigint
            var id = reader.GetInt64(reader.GetOrdinal("Id"));
            var name = reader.GetString(reader.GetOrdinal("Name"));
            var typeByte = reader.GetByte(reader.GetOrdinal("Type"));
            var messageCount = reader.GetInt64(reader.GetOrdinal("MessageCount"));

            if (string.IsNullOrEmpty(name))
                continue;

            // Determine canonical base name for grouping (strip transfer suffix and any separator before it)
            string baseName = name;
            if (name.EndsWith(transferSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var pos = name.Length - transferSuffix.Length;
                if (pos > 0 && separators.Contains(name[pos - 1])) pos--;
                baseName = name.Substring(0, pos);
            }

            baseName = baseName.TrimEnd(separators);

            // Map type byte to human-friendly entity type
            var entityType = typeByte switch
            {
                1 => "Topic",
                2 => "Subscription",
                _ => "Queue"
            };

            var map = string.Equals(entityType, "Topic", StringComparison.OrdinalIgnoreCase) ? topics : queues;

            if (name.EndsWith(transferSuffix, StringComparison.OrdinalIgnoreCase))
            {
                if (!map.TryGetValue(baseName, out var tuple)) tuple = (Id: id, Active: 0, Deadletter: 0);
                tuple.Deadletter = messageCount;
                if (tuple.Id == 0) tuple.Id = id;
                map[baseName] = tuple;
            }
            else
            {
                if (!map.TryGetValue(baseName, out var tuple)) tuple = (Id: id, Active: 0, Deadletter: 0);
                tuple.Active = messageCount;
                if (tuple.Id == 0) tuple.Id = id;
                map[baseName] = tuple;
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

        return results;
    }
}
