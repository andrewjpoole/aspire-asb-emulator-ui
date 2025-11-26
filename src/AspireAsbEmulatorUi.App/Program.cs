using AspireAsbEmulatorUi.App.Components;
using AspireAsbEmulatorUi.App.Services;
using AspireAsbEmulatorUi.App.Api;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register application repository via DI so it can receive logging and other services
builder.Services.AddSingleton<AsbEmulatorSqlEntityRepository>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<AsbEmulatorSqlEntityRepository>>();
    var repo = new AsbEmulatorSqlEntityRepository(logger);

    // Prefer a full connection string if provided
    var cs = cfg["asb-sql-connectionstring"] ?? cfg["ASB_SQL_CONNECTIONSTRING"];
    if (string.IsNullOrWhiteSpace(cs))
    {
        // Assemble connection string from port and password passed by AppHost
        var port = cfg["asb-sql-port"] ?? cfg["ASB_SQL_PORT"];
        var pwd = cfg["asb-sql-password"] ?? cfg["ASB_SQL_PASSWORD"];

        if (!string.IsNullOrWhiteSpace(port) && !string.IsNullOrWhiteSpace(pwd))
        {
                // Build a list of host candidates in priority order
                var candidates = new List<string>();

                // Prefer an explicit host provided by the hosting resource (exposed via environment variable)
                var explicitHost = cfg["asb-sql-host"] ?? cfg["ASB_SQL_HOST"];
                if (!string.IsNullOrWhiteSpace(explicitHost))
                {
                    candidates.Add(explicitHost);
                }

                // Try service-name-based host (the hosting extension names the SQL container as `{resourceName}-mssql`)
                var resourceName = cfg["asb-resource-name"] ?? cfg["ASB_RESOURCE_NAME"] ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(resourceName))
                {
                    candidates.Add($"{resourceName}-mssql");
                }

                // Fallbacks useful for local/dev/container scenarios
                candidates.Add("host.docker.internal"); // from within docker container on dockerdesktop
                candidates.Add("host.containers.internal"); // from within diocker container on podman
                candidates.Add("127.0.0.1");

                string selectedHost = candidates.First();
                string selectedConn = string.Empty;
                foreach (var candidate in candidates)
                {
                    var candidateConn = $"Server={candidate},{port};Database=SbMessageContainerDatabase00001;User Id=sa;Password={pwd};TrustServerCertificate=True;";
                    // Mask the password for logging
                    var masked = Regex.Replace(candidateConn, "(?i)(Password|Pwd)=[^;]+", "$1=****");
                    logger.LogInformation("Probing SQL candidate: {Candidate}", masked);
                    if (TrySqlConnect(candidateConn, 1500, out var error))
                    {
                        logger.LogInformation("SQL candidate succeeded: {Candidate}", masked);
                        selectedHost = candidate;
                        selectedConn = candidateConn;
                        break;
                    }
                    else
                    {
                        logger.LogDebug(error ?? "Unknown error", Array.Empty<object>());
                        logger.LogInformation("SQL candidate failed: {Candidate}", masked);
                    }
                }

                cs = string.IsNullOrEmpty(selectedConn)
                    ? $"Server={selectedHost},{port};Database=SbMessageContainerDatabase00001;User Id=sa;Password={pwd};TrustServerCertificate=True;"
                    : selectedConn;
        }
    }

    if (!string.IsNullOrWhiteSpace(cs)) repo.SetConnectionString(cs);
    return repo;
});

builder.Services.AddSingleton<ServiceBusService>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<ServiceBusService>>();
    var repo = sp.GetRequiredService<AsbEmulatorSqlEntityRepository>();

    // Get the resource name to build the connection string key
    var resourceName = cfg["asb-resource-name"] ?? cfg["ASB_RESOURCE_NAME"] ?? "myservicebus";
    logger.LogInformation("ASB Resource Name: {ResourceName}", resourceName);

    // Build the connection string key (Aspire convention: ConnectionStrings__{ResourceName})
    var connectionStringKey = $"ConnectionStrings__{resourceName}";

    // Try various formats for the connection string
    var cs = cfg[connectionStringKey]
             ?? cfg[$"ConnectionStrings:{resourceName}"];

    if (string.IsNullOrWhiteSpace(cs))    
    {
        logger.LogWarning("No ASB connection string found for resource: {ResourceName}", resourceName);
    }

    var service = new ServiceBusService(cs ?? string.Empty, logger, repo);
    
    return service;
});

builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<PlaceholderService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    // Keep HSTS in non-development environments.
    app.UseHsts();
}

// Removed custom error and not-found pages so the app exposes only the Home page.
app.UseHttpsRedirection();

app.UseAntiforgery();

// Map integration test API endpoints (if enabled)
app.MapIntegrationTestEndpoints();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static bool TrySqlConnect(string connStr, int timeoutMs, out string? error)
{
    error = null;
    try
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var conn = new SqlConnection(connStr);
        var task = conn.OpenAsync(cts.Token);

        // Wait for the open to complete or timeout
        if (task.Wait(timeoutMs))
        {
            return conn.State == System.Data.ConnectionState.Open;
        }

        error = "Timeout while attempting to open SQL connection.";
        return false;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}