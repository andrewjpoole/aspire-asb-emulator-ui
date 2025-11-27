using AspireAsbEmulatorUi.App.Components;
using AspireAsbEmulatorUi.App.Services;
using AspireAsbEmulatorUi.App.Api;
using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register application repository via DI so it can receive logging and other services
builder.Services.AddSingleton(sp =>
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
        var asbEmulatorSqlServer = cfg["asb-emulator-sqlserver"] ?? cfg["ASB_EMULATOR_SQLSERVER"];

        if (!string.IsNullOrWhiteSpace(pwd) && (!string.IsNullOrWhiteSpace(port) || !string.IsNullOrWhiteSpace(asbEmulatorSqlServer)))
        {
            // Build a list of host candidates in priority order (host + optional per-candidate port)
            var candidates = new List<(string Host, string? Port)>();

            // If hosting provided ASB_EMULATOR_SQLSERVER (format host:port), prefer that and use its port
            if (!string.IsNullOrWhiteSpace(asbEmulatorSqlServer))
            {
                var parts = asbEmulatorSqlServer.Split(':', 2);
                var h = parts[0];
                string? p = parts.Length > 1 ? parts[1] : null;
                candidates.Add((h, p));
            }            

            // Fallbacks useful for local/dev/container scenarios
            candidates.Add(("host.docker.internal", null));
            //candidates.Add(("host.containers.internal", null));
            candidates.Add(("127.0.0.1", null));

            string selectedHost = candidates.First().Host;
            string selectedConn = string.Empty;
            foreach (var candidate in candidates)
            {
                var candidatePort = candidate.Port ?? port;
                var candidateConn = $"Server={candidate.Host},{candidatePort};Database=SbMessageContainerDatabase00001;User Id=sa;Password={pwd};TrustServerCertificate=True;";
                // Mask the password for logging
                var masked = Regex.Replace(candidateConn, "(?i)(Password|Pwd)=[^;]+", "$1=****");
                logger.LogInformation("Probing SQL candidate: {Candidate}", masked);
                if (TrySqlConnect(candidateConn, 1500, out var error))
                {
                    logger.LogInformation("SQL candidate succeeded");
                    selectedHost = candidate.Host + (candidate.Port != null ? ":" + candidate.Port : string.Empty);
                    selectedConn = candidateConn;
                    break;
                }
                else
                {
                    logger.LogDebug(error ?? "Unknown error", Array.Empty<object>());
                    logger.LogInformation("SQL candidate failed");
                }
            }

            if (string.IsNullOrEmpty(selectedConn))
            {
                var tried = string.Join(", ", candidates.Select(c => c.Port != null ? $"{c.Host}:{c.Port}" : c.Host));
                logger.LogError("No SQL candidate succeeded. Candidates tried: {Candidates}", tried);
                throw new InvalidOperationException($"Could not connect to SQL server using any candidates: {tried}. Check asb-sql-host, asb-sql-port, and asb-sql-password settings.");
            }

            cs = selectedConn;
        }
    }

    if (!string.IsNullOrWhiteSpace(cs)) 
        repo.SetConnectionString(cs);
    
    return repo;
});

builder.Services.AddSingleton(sp =>
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

builder.Services.AddScoped<SettingsService>();
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