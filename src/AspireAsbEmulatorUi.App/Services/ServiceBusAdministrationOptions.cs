using System.Data.Common;
using Azure.Messaging.ServiceBus.Administration;

namespace AspireAsbEmulatorUi.App.Services;

public enum EntityDiscoveryMode { Auto, Administration, Sql }

public sealed class ServiceBusAdministrationOptions
{
    public EntityDiscoveryMode DiscoveryMode { get; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
    public bool HasSqlConfiguration { get; }
    internal string? ConnectionString { get; }
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    public ServiceBusAdministrationOptions(IConfiguration configuration)
    {
        var mode = Get(configuration, "entity-discovery") ?? "Auto";
        if (!Enum.TryParse<EntityDiscoveryMode>(mode, true, out var parsed) || !Enum.IsDefined(parsed))
            throw new InvalidOperationException("ASB_ENTITY_DISCOVERY must be Auto, Administration or Sql.");
        DiscoveryMode = parsed;
        HasSqlConfiguration = !string.IsNullOrWhiteSpace(Get(configuration, "sql-connectionstring")) ||
            (!string.IsNullOrWhiteSpace(Get(configuration, "sql-password")) &&
             (!string.IsNullOrWhiteSpace(Get(configuration, "sql-port")) ||
              !string.IsNullOrWhiteSpace(Get(configuration, "emulator-sqlserver"))));

        try
        {
            var connectionString = Get(configuration, "management-connectionstring");
            var endpoint = Get(configuration, "management-endpoint");
            if (!string.IsNullOrWhiteSpace(connectionString))
                ConnectionString = connectionString;
            else if (!string.IsNullOrWhiteSpace(endpoint))
            {
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
                    uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                    throw new ArgumentException();

                var builder = new DbConnectionStringBuilder
                {
                    ConnectionString = GetDataConnectionString(configuration) ??
                        "SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;"
                };
                builder["Endpoint"] = $"sb://{uri.Authority}";
                builder["UseDevelopmentEmulator"] = uri.Scheme == "http";
                builder.Remove("EntityPath");
                ConnectionString = builder.ConnectionString;
            }

            if (IsConfigured) _ = CreateClient();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // SDK parse errors can contain the connection string.
            throw new InvalidOperationException("Invalid Service Bus management configuration. Check ASB_MANAGEMENT_ENDPOINT and ASB_MANAGEMENT_CONNECTIONSTRING.");
        }

        if (DiscoveryMode == EntityDiscoveryMode.Administration && !IsConfigured)
            throw new InvalidOperationException("Administration discovery requires ASB_MANAGEMENT_ENDPOINT or ASB_MANAGEMENT_CONNECTIONSTRING.");
    }

    internal ServiceBusAdministrationClient? CreateClient()
    {
        if (!IsConfigured) return null;
        var options = new ServiceBusAdministrationClientOptions();
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = OperationTimeout;
        return new ServiceBusAdministrationClient(ConnectionString, options);
    }

    internal static string? GetDataConnectionString(IConfiguration configuration)
    {
        var name = Get(configuration, "resource-name") ?? "myservicebus";
        return configuration[$"ConnectionStrings__{name}"] ?? configuration.GetConnectionString(name);
    }

    private static string? Get(IConfiguration configuration, string suffix) =>
        configuration[$"asb-{suffix}"] ?? configuration[$"ASB_{suffix.Replace('-', '_').ToUpperInvariant()}"];
}
