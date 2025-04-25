using Amazon.S3;
using Amazon.SQS;
using MetaMorphAPI.Services;
using MetaMorphAPI.Services.Cache;
using MetaMorphAPI.Services.Queue;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.StaticFiles;
using Prometheus;
using Serilog;
using StackExchange.Redis;

namespace MetaMorphAPI.Utils;

/// <summary>
/// Contains extension methods for configuring application services, logging, caching,
/// file handling, and setting up local or remote infrastructure within a .NET application.
/// </summary>
public static class BootstrapHelper
{
    private const string TEMP_DIRECTORY_NAME = "metamorph";
    private const string CONVERTED_DIRECTORY_NAME = "converted";
    private const string KTX2_MIME_TYPE = "image/ktx2";

    public static void SetupSerilog(this IHostApplicationBuilder builder)
    {
        // Bootstrap the static Serilog logger for use in Program.cs
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .CreateBootstrapLogger();
        // Used only after bootstrap
        builder.Services.AddSerilog((_, lc) => lc.ReadFrom.Configuration(builder.Configuration));
    }

    public static void SetupConverter(this IHostApplicationBuilder builder)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), TEMP_DIRECTORY_NAME);
        Directory.CreateDirectory(tempDirectory);

        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<FileAnalyzerService>();
        builder.Services.AddSingleton<ConverterService>(sp =>
            new ConverterService(
                tempDirectory,
                sp.GetRequiredService<FileAnalyzerService>(),
                sp.GetRequiredService<ILogger<ConverterService>>()));

        builder.Services.AddSingleton<DownloadService>(sp =>
            new DownloadService(
                tempDirectory,
                sp.GetRequiredService<HttpClient>()));

        builder.Services.AddHostedService<ConversionBackgroundService>();
    }

    internal static void SetupLocalCache(this WebApplicationBuilder builder)
    {
        var localConvertedDirectory = Path.Combine(
            builder.Environment.WebRootPath,
            CONVERTED_DIRECTORY_NAME);

        Directory.CreateDirectory(localConvertedDirectory);

        builder.Services.AddSingleton<ICacheService, LocalCacheService>(sp =>
            new LocalCacheService(
                localConvertedDirectory,
                sp.GetRequiredService<ILogger<LocalCacheService>>()));

        builder.Services.AddSingleton<IConversionQueue, LocalConversionQueue>();
    }

    public static void SetupRemoteCache(this IHostApplicationBuilder builder)
    {
        // Params
        var awsServiceUrl = builder.GetRequiredConfig<string>("AWS:ServiceURL");
        var sqsQueueUrl = builder.GetRequiredConfig<string>("AWS:SQSQueueURL");
        var s3BucketName = builder.GetRequiredConfig<string>("AWS:S3BucketName");
        var s3ForcePathStyle = builder.GetRequiredConfig<bool>("AWS:S3ForcePathStyle");
        var redisConnectionString = builder.GetRequiredConfig<string>("Redis:ConnectionString");

        // Redis
        builder.Services.AddSingleton<ConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnectionString));

        // SQS
        var sqsClient = new AmazonSQSClient(new AmazonSQSConfig { ServiceURL = awsServiceUrl });
        builder.Services.AddSingleton<IAmazonSQS>(sqsClient);
        builder.Services.AddSingleton<IConversionQueue>(sp =>
            new RemoteConversionQueue(
                sqsClient,
                sqsQueueUrl,
                sp.GetRequiredService<ConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RemoteConversionQueue>>()));

        // S3
        var s3Client = new AmazonS3Client(new AmazonS3Config
        {
            ServiceURL = awsServiceUrl,
            ForcePathStyle = s3ForcePathStyle
        });
        builder.Services.AddSingleton<IAmazonS3>(s3Client);

        // Register the RemoteCacheService
        builder.Services.AddSingleton<ICacheService>(sp =>
            new RemoteCacheService(
                sp.GetRequiredService<IAmazonS3>(),
                s3BucketName,
                sp.GetRequiredService<ConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RemoteCacheService>>()
            ));
    }

    public static void SetupStaticFiles(this WebApplication app)
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            ContentTypeProvider = new FileExtensionContentTypeProvider
            {
                Mappings =
                {
                    [".ktx2"] = KTX2_MIME_TYPE
                }
            }
        });
    }

    public static async Task SetupLocalInfrastructureAsync(this WebApplication app, IHostApplicationBuilder builder)
    {
        using var scope = app.Services.CreateScope();

        if (builder.GetRequiredConfig<bool>("MetaMorph:StartLocalInfra"))
        {
            await LocalInfra.EnsureDockerRunningAsync();
            await LocalInfra.EnsureLocalStackRunningAsync();
            await LocalInfra.EnsureRedisRunningAsync();
        }

        await LocalInfra.SetupLocalStack(
            scope,
            builder.GetRequiredConfig<string>("AWS:S3BucketName"),
            builder.GetRequiredConfig<string>("AWS:SQSQueueName")
        );
    }

    public static void SetupMetrics(this WebApplication app)
    {
        app.MapMetrics();
        app.UseHttpMetrics();
    }

    public static void SetupHealthCheck(this WebApplication app)
    {
        // Health endpoint for docker readiness check
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            // Don’t run any checks—just return Healthy
            Predicate = _ => false,

            ResponseWriter = async (context, _) =>
            {
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync("OK");
            }
        });
    }

    public static T GetRequiredConfig<T>(this IHostApplicationBuilder builder, string key)
    {
        var section = builder.Configuration.GetSection(key);
        if (!section.Exists())
            throw new InvalidOperationException($"Missing required configuration: {key}");

        return section.Get<T>()!;
    }
}