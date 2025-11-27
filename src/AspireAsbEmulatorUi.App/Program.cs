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

            string selectedConn = string.Empty;
            
            // Run probing in a background thread to avoid deadlocking the Blazor SyncContext
            var probeResult = Task.Run(async () => 
            {
                var probeTasks = candidates.Select(async candidate => 
                {
                    var candidatePort = candidate.Port ?? port;
                    var candidateConn = $"Server={candidate.Host},{candidatePort};Database=SbMessageContainerDatabase00001;User Id=sa;Password={pwd};TrustServerCertificate=True;";
                    var masked = Regex.Replace(candidateConn, "(?i)(Password|Pwd)=[^;]+", "$1=****");
                    logger.LogInformation("Probing SQL candidate: {Candidate}", masked);
                    
                    var (success, error) = await TrySqlConnectAsync(candidateConn, 1500).ConfigureAwait(false);
                    
                    if (!success)
                    {
                         logger.LogDebug("Probe failed for {Candidate}: {Error}", masked, error);
                    }
                    
                    return new { Candidate = candidate, ConnectionString = candidateConn, Success = success, Error = error };
                }).ToList();

                while (probeTasks.Count > 0)
                {
                    var completedTask = await Task.WhenAny(probeTasks).ConfigureAwait(false);
                    probeTasks.Remove(completedTask);
                    var result = await completedTask.ConfigureAwait(false);
                    
                    if (result.Success)
                    {
                        return result;
                    }
                }
                return null;
            }).Result;

            if (probeResult != null)
            {
                logger.LogInformation("SQL candidate succeeded: {Host}", probeResult.Candidate.Host);
                selectedConn = probeResult.ConnectionString;
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

static async Task<(bool Success, string? Error)> TrySqlConnectAsync(string connStr, int timeoutMs)
{
    try
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(cts.Token).ConfigureAwait(false);
        return (true, null);
    }
    catch (Exception ex)
    {
        return (false, ex.Message);
    }
}