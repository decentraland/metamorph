using MetaMorphAPI.Utils;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure logging
builder.SetupSerilog();

// Add controllers to the application
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
    builder.SetupLocalCache();
}
else
{
    builder.SetupRemoteCache();
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

// Map API controllers
app.MapControllers();

// For the load balancer
app.SetupHealthCheck();

app.Run();