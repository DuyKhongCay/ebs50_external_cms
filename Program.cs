using ebs50_backend.Data;
using ebs50_backend.Services;
using Microsoft.EntityFrameworkCore;
using ebs50_backend.Services.Networking;
using ebs50_backend.Services.Dispatching;
using Microsoft.AspNetCore.RateLimiting;
using ebs50_backend.Services.Database;

// NOTE: Windows Service execution sets CurrentDirectory to C:\Windows\System32 by default.
// Explicitly set CurrentDirectory to AppContext.BaseDirectory to guarantee consistent file resolution.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);

// Ensure WebRootPath is properly located whether running via 'dotnet run', direct .exe, or Windows Service
if (string.IsNullOrEmpty(builder.Environment.WebRootPath) || !Directory.Exists(builder.Environment.WebRootPath))
{
    var candidateInBaseDir = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    var candidateInProject = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot"));

    if (Directory.Exists(candidateInBaseDir))
    {
        builder.Environment.WebRootPath = candidateInBaseDir;
    }
    else if (Directory.Exists(candidateInProject))
    {
        builder.Environment.WebRootPath = candidateInProject;
    }
}

// NOTE: Support running as a native Windows Service with explicit service name
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "Ebs50TagService";
});

// Configure Windows Event Log provider for production operations on Windows platform
if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog(settings =>
    {
        if (OperatingSystem.IsWindows())
        {
            settings.SourceName = "Ebs50TagService";
        }
    });
}

// Configure graceful shutdown deadline to 30 seconds
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});

// Configure Kestrel to listen on all IP addresses in LAN on port 6789
var listeningPort = builder.Configuration.GetValue<int>("ServiceSettings:Port", 6789);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(listeningPort);
});

// Share canonical file resolution between EF, online backup and startup recovery.
var databaseLocation = new DatabaseLocation(builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=etag_database.db");
foreach (var publicDirectory in new[] { builder.Environment.WebRootPath,
    Path.Combine(builder.Environment.ContentRootPath, "Assets"), Path.Combine(AppContext.BaseDirectory, "Assets") })
{
    if (string.IsNullOrEmpty(publicDirectory)) continue;
    var publicRoot = Path.GetFullPath(publicDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (databaseLocation.DatabasePath.StartsWith(publicRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Store SQLite and maintenance backups outside publicly served directories.");
}
builder.Services.AddSingleton(databaseLocation);
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(databaseLocation.ConnectionString));
builder.Services.AddSingleton<DatabaseMaintenanceCoordinator>();
builder.Services.AddSingleton<DatabaseBackupFiles>();
builder.Services.AddSingleton<DatabaseMaintenanceService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DatabaseMaintenanceService>());
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

builder.Services.AddScoped<IDbInitializer, DbInitializer>();
builder.Services.AddScoped<ebs50_backend.Services.Rendering.ITagRenderService, ebs50_backend.Services.Rendering.SkiaTagRenderService>();
builder.Services.AddSingleton<ebs50_backend.Services.Dispatching.IEslDispatchQueue, ebs50_backend.Services.Dispatching.EslDispatchQueue>();
builder.Services.AddScoped<ebs50_backend.Services.Dispatching.IEbs50SftpService, ebs50_backend.Services.Dispatching.Ebs50SftpService>();
builder.Services.AddScoped<ebs50_backend.Services.Dispatching.IEbs50LinkSyncService, ebs50_backend.Services.Dispatching.Ebs50LinkSyncService>();
builder.Services.AddScoped<ebs50_backend.Services.Dispatching.IEbs50StatusService, ebs50_backend.Services.Dispatching.Ebs50StatusService>();

builder.Services.AddHostedService<ebs50_backend.Services.Dispatching.EslDispatcherBackgroundWorker>();
builder.Services.AddHostedService<ebs50_backend.Services.Dispatching.Ebs50StatusReconciliationWorker>();

builder.Services.AddScoped<ebs50_backend.Services.Core.ITagService, ebs50_backend.Services.Core.TagService>();
builder.Services.AddScoped<ebs50_backend.Services.Core.TagManager>();
builder.Services.AddScoped<ebs50_backend.Services.Core.ITagManager, ebs50_backend.Services.Core.ResilientTagManager>();

builder.Services.AddRazorPages();
builder.Services.AddScoped<IEbs50ConnectionSettingsProvider, Ebs50ConnectionSettingsProvider>();
builder.Services.AddSingleton<INetworkInterfaceService, NetworkInterfaceService>();
builder.Services.AddScoped<INetworkDiagnosticsService, NetworkDiagnosticsService>();
builder.Services.AddHttpClient("NetworkDiagnostics", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddConcurrencyLimiter("network-diagnostics", limiter =>
    {
        limiter.PermitLimit = 1;
        limiter.QueueLimit = 0;
    });
});
builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "ebs50_backend.xml"));
    c.OperationFilter<ebs50_backend.OpenApi.ApiContractOperationFilter>();
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "E-Tag EBS-50 Management & MES API",
        Version = "v1",
        Description = "REST API for Opticon EBS-50 External CMS E-Tag management and MES manufacturing execution integration."
    });
});

var app = builder.Build();

// Recovery must finish before initialization or any worker can access the database.
await app.Services.GetRequiredService<DatabaseMaintenanceService>().RecoverOnStartupAsync(CancellationToken.None);

// NOTE: Perform asynchronous database creation and data seeding prior to serving web requests
using (var scope = app.Services.CreateScope())
{
    var dbInitializer = scope.ServiceProvider.GetRequiredService<IDbInitializer>();
    await dbInitializer.InitializeAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "E-Tag API v1");
    c.RoutePrefix = "swagger";
});

app.UseStaticFiles();

// Serve /Assets directory for template and icon assets
var assetsPath = Path.Combine(builder.Environment.ContentRootPath, "Assets");
if (!Directory.Exists(assetsPath))
{
    assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
}
if (Directory.Exists(assetsPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(assetsPath),
        RequestPath = "/Assets"
    });
}

app.UseRouting();
app.UseMiddleware<DatabaseMaintenanceMiddleware>();
app.UseRateLimiter();

app.MapGet("/health/live", () => Results.Ok(new { status = "Alive" }))
    .WithName("Liveness").WithSummary("Reports web process liveness, independent of EBS connectivity.");

app.MapRazorPages();
app.MapControllers();

app.Run();
