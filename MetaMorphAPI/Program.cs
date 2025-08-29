using MetaMorphAPI.Utils;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Sentry: read from configuration & env vars SENTRY_DSN
builder.WebHost.UseSentry();

builder.Services.AddHttpClient();

// Configure logging
builder.SetupSerilog();

// Add controllers and a health check
builder.SetupHealthChecks();
builder.Services.AddControllers();

// Configure a local worker if required
var localWorker = builder.GetRequiredConfig<bool>("MetaMorph:LocalWorker");
Log.Information("Using local worker: {LocalWorker}", localWorker);
if (localWorker)
{
    builder.SetupConverter();
}

// Configure storage/cache based on configuration
var localCache = builder.GetRequiredConfig<bool>("MetaMorph:LocalCache");
Log.Information("Using local cache: {LocalCache}", localCache);
if (localCache)
{
    Log.Warning("Local cache mode does not handle concurrent requests for the same URL well.");
    builder.SetupLocalCache();
}
else
{
    builder.SetupRemoteCache(localWorker ||
                             builder.Environment.IsDevelopment()); // With local worker we need to setup S3
}

var app = builder.Build();

// Configure static files for serving content
if (localCache)
{
    app.SetupStaticFiles();
}

// Configure LocalStack for development environment
if (!localCache && app.Environment.IsDevelopment())
{
    await app.SetupLocalInfrastructureAsync(builder);
}

// Metrics
app.SetupMetrics();

// Map API controllers
app.MapControllers();

// For the load balancer
app.SetupHealthCheck();

app.Run();