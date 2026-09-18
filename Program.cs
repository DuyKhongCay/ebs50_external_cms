using ebs50_backend.Data;
using ebs50_backend.Services;
using Microsoft.EntityFrameworkCore;

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

// Register SQLite database context with explicit path anchoring
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                           ?? "Data Source=etag_database.db";

    // NOTE: Anchor relative SQLite DB path to AppContext.BaseDirectory to prevent Windows Service
    // from attempting to create or access etag_database.db inside C:\Windows\System32.
    if (connectionString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
    {
        var dataSource = connectionString.Substring("Data Source=".Length).Trim();
        if (!Path.IsPathRooted(dataSource) && !dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            var absoluteDbPath = Path.Combine(AppContext.BaseDirectory, dataSource);
            connectionString = $"Data Source={absoluteDbPath}";
        }
    }

    options.UseSqlite(connectionString);
});

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

app.MapRazorPages();
app.MapControllers();

app.Run();
