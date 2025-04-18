using Amazon.S3;
using Amazon.SQS;
using MetaMorphAPI.Services;
using MetaMorphAPI.Services.Cache;
using MetaMorphAPI.Services.Queue;
using MetaMorphAPI.Utils;
using Microsoft.AspNetCore.StaticFiles;
using Serilog;
using Serilog.Events;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Logging
// Bootstrap the static Serilog logger for use in Program.cs
Log.Logger = new LoggerConfiguration()
    // .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger(); // <-- Change this line!
builder.Services.AddSerilog((services, lc) => lc.ReadFrom.Configuration(builder.Configuration));

// Paths
var tempDirectory = Path.Combine(builder.Environment.ContentRootPath, "temp");
var localConvertedDirectory = Path.Combine(builder.Environment.WebRootPath, "converted");
Directory.CreateDirectory(tempDirectory);
Directory.CreateDirectory(localConvertedDirectory);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<FileAnalyzerService>();
builder.Services.AddSingleton<ConverterService>(sp =>
    new ConverterService(tempDirectory, sp.GetRequiredService<FileAnalyzerService>(),
        sp.GetRequiredService<ILogger<ConverterService>>()));
builder.Services.AddSingleton<DownloadService>(sp =>
    new DownloadService(tempDirectory, sp.GetRequiredService<HttpClient>()));

var localWorker = builder.Configuration["LOCAL_WORKER"] == "true";
if (localWorker)
{
    Log.Information("Using local worker");
    builder.Services.AddHostedService<ConversionBackgroundService>();
}

// Setup storage mode from configuration.
var localMode = builder.Configuration["CACHE_MODE"] != "Remote";

if (localMode)
{
    // Default to local mode.
    Log.Information("Using local mode");
    builder.Services.AddSingleton<ICacheService, LocalCacheService>(sp =>
        new LocalCacheService(localConvertedDirectory, sp.GetRequiredService<ILogger<LocalCacheService>>()));
    builder.Services.AddSingleton<IConversionQueue, LocalConversionQueue>();
}
else
{
    Log.Information("Using remote mode");

    // Params
    var awsServiceUrl = GetRequiredConfig<string>("AWS:ServiceURL");
    var sqsQueueUrl = GetRequiredConfig<string>("AWS:SQSQueueURL");
    var s3BucketName = GetRequiredConfig<string>("AWS:S3BucketName");
    var s3ForcePathStyle = GetRequiredConfig<bool>("AWS:S3ForcePathStyle");
    var redisConnectionString = GetRequiredConfig<string>("Redis:ConnectionString");

    // Redis
    builder.Services.AddSingleton<ConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));

    // SQS
    var sqsConfig = new AmazonSQSConfig { ServiceURL = awsServiceUrl };
    var sqsClient = new AmazonSQSClient(sqsConfig);
    builder.Services.AddSingleton<IAmazonSQS>(sqsClient);
    builder.Services.AddSingleton<IConversionQueue>(sp => new RemoteConversionQueue(sqsClient, sqsQueueUrl,
        sp.GetRequiredService<ConnectionMultiplexer>(), sp.GetRequiredService<ILogger<RemoteConversionQueue>>()));

    // S3
    var s3Config = new AmazonS3Config
    {
        ServiceURL = awsServiceUrl,
        ForcePathStyle = s3ForcePathStyle
    };
    var s3Client = new AmazonS3Client(s3Config);
    builder.Services.AddSingleton<IAmazonS3>(s3Client);

    // Register the RemoteCacheService with all its dependencies.
    builder.Services.AddSingleton<ICacheService>(sp => new RemoteCacheService(
        sp.GetRequiredService<IAmazonS3>(),
        s3BucketName, sp.GetRequiredService<ConnectionMultiplexer>(),
        sp.GetRequiredService<ILogger<RemoteCacheService>>()
    ));
}

var app = builder.Build();

if (localMode)
{
    // Setup content types when serving local files
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
if (!localMode && app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();

    if (builder.Configuration["START_LOCAL_INFRA"] == "true")
    {
        await LocalInfra.EnsureLocalStackRunningAsync();
        await LocalInfra.EnsureRedisRunningAsync();
    }

    await LocalInfra.SetupLocalStack(scope,
        GetRequiredConfig<string>("AWS:S3BucketName"),
        GetRequiredConfig<string>("AWS:SQSQueueName")
    );
    
    // Health endpoint for readiness checks
    app.MapGet("/health", () => Results.Ok());
}

app.MapControllers();

app.Run();

return;

T GetRequiredConfig<T>(string key)
{
    var section = builder.Configuration.GetSection(key);
    if (!section.Exists())
        throw new InvalidOperationException($"Missing required configuration: {key}");

    return section.Get<T>()!;
}