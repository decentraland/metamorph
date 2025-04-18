using MetaMorphAPI.Utils;
using Microsoft.AspNetCore.StaticFiles;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Logging
builder.SetupSerilog();

// Create temp dir
var tempDirectory = Path.Combine(builder.Environment.ContentRootPath, "temp");
Directory.CreateDirectory(tempDirectory);

// Add services to the container.
builder.Services.AddControllers();

// Setup local worker if required
var localWorker = builder.GetRequiredConfig<bool>("MetaMorph:LocalWorker");
Log.Information("Using local worker: {LocalWorker}", localWorker);
if (localWorker)
{
    builder.SetupConverter();
}

// Setup storage / cache mode from configuration.
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

// Setup content types when serving local files
if (localCache)
{
    app.UseStaticFiles(new StaticFileOptions
    {
        ContentTypeProvider = new FileExtensionContentTypeProvider
        {
            Mappings =
            {
                [".ktx2"] = "image/ktx2"
            }
        }
    });
}

// Setup LocalStack
if (!localCache && app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();

    if (builder.GetRequiredConfig<bool>("MetaMorph:StartLocalInfra"))
    {
        await LocalInfra.EnsureLocalStackRunningAsync();
        await LocalInfra.EnsureRedisRunningAsync();
    }

    await LocalInfra.SetupLocalStack(scope,
        builder.GetRequiredConfig<string>("AWS:S3BucketName"),
        builder.GetRequiredConfig<string>("AWS:SQSQueueName")
    );

    // Health endpoint for docker readiness check
    app.MapGet("/health", () => Results.Ok());
}

app.MapControllers();

app.Run();