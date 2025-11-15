using AspireAsbEmulatorUi.App.Components;
using AspireAsbEmulatorUi.App.Services;
using System.Net.Sockets;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register application services
builder.Services.AddSingleton(sp =>
{
    var repo = new AsbEmulatorSqlEntityRepository();
    var cfg = sp.GetRequiredService<IConfiguration>();

    // Prefer a full connection string if provided
    var cs = cfg["asb-sql-connectionstring"] ?? cfg["ASB_SQL_CONNECTIONSTRING"];
    if (string.IsNullOrWhiteSpace(cs))
    {
        // Assemble connection string from port and password passed by AppHost
        var port = cfg["asb-sql-port"] ?? cfg["ASB_SQL_PORT"];
        var pwd = cfg["asb-sql-password"] ?? cfg["ASB_SQL_PASSWORD"];

        if (!string.IsNullOrWhiteSpace(port) && !string.IsNullOrWhiteSpace(pwd))
        {
            // Try 127.0.0.1 first, then fall back to host.docker.internal (useful when running in containers)
            string hostCandidate1 = "127.0.0.1";
            string hostCandidate2 = "host.docker.internal";
            string selectedHost = hostCandidate1;
            if (!TryTcpConnect(hostCandidate1, port, 1500))
            {
                // attempt fallback
                if (TryTcpConnect(hostCandidate2, port, 1500))
                {
                    selectedHost = hostCandidate2;
                }
                else
                {
                    // neither reachable; still use 127.0.0.1 to surface connection errors later
                    selectedHost = hostCandidate1;
                }
            }

            cs = $"Server={selectedHost},{port};Database=SbMessageContainerDatabase00001;User Id=sa;Password={pwd};TrustServerCertificate=True;";
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
    
    if (!string.IsNullOrWhiteSpace(cs))
    {
        // Log the connection string format (without sensitive data)
        var csPreview = cs.Length > 50 ? cs.Substring(0, 50) + "..." : cs;
        logger.LogInformation("ASB Connection String (preview): {ConnectionString}", csPreview);
    }
    else
    {
        logger.LogWarning("No ASB connection string found for resource: {ResourceName}", resourceName);
    }
    
    return new ServiceBusService(cs ?? string.Empty, logger, repo);
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

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static bool TryTcpConnect(string host, string portText, int timeoutMs)
{
    if (!int.TryParse(portText, out var port)) return false;
    try
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var tcp = new TcpClient();
        var task = tcp.ConnectAsync(host, port);
        // Wait for completion or timeout
        if (task.Wait(timeoutMs))
        {
            return tcp.Connected;
        }
        return false;
    }
    catch
    {
        return false;
    }
}
