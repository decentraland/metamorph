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

    public static void SetupHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks();
    }

    public static void SetupConverter(this IHostApplicationBuilder builder)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), TEMP_DIRECTORY_NAME);
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, true);
        }

        Directory.CreateDirectory(tempDirectory);

        builder.Services.AddSingleton<FileAnalyzerService>();
        builder.Services.AddSingleton<ConverterService>(sp =>
            new ConverterService(
                tempDirectory,
                sp.GetRequiredService<FileAnalyzerService>(),
                sp.GetRequiredService<ILogger<ConverterService>>()));

        builder.Services.AddSingleton<DownloadService>(sp =>
            new DownloadService(
                tempDirectory,
                sp.GetRequiredService<HttpClient>(),
                builder.GetRequiredConfig<int>("MetaMorph:MaxDownloadFileSizeMB") * 1024L * 1024L
            ));

        builder.Services.AddHostedService<ConversionBackgroundService>(sp =>
            new ConversionBackgroundService(
                sp.GetRequiredService<IConversionQueue>(),
                sp.GetRequiredService<ConverterService>(),
                sp.GetRequiredService<DownloadService>(),
                sp.GetRequiredService<ICacheService>(),
                builder.GetRequiredConfig<int>("MetaMorph:ConcurrentConversions"),
                sp.GetRequiredService<ILogger<ConversionBackgroundService>>()
            ));
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

    public static void SetupRemoteCache(this IHostApplicationBuilder builder, bool setupS3)
    {
        builder.Services.AddDefaultAWSOptions(builder.Configuration.GetAWSOptions());

        // Params
        var sqsQueueName = builder.GetRequiredConfig<string>("AWS:SQSQueueName");
        var redisConnectionString = builder.GetRequiredConfig<string>("Redis:ConnectionString");

        // Redis
        builder.Services.AddSingleton<ConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnectionString));

        // SQS
        builder.Services.AddAWSService<IAmazonSQS>();
        builder.Services.AddSingleton<IConversionQueue>(sp =>
            new RemoteConversionQueue(
                sp.GetRequiredService<IAmazonSQS>(),
                sqsQueueName,
                sp.GetRequiredService<ConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RemoteConversionQueue>>()));

        // S3
        if (setupS3)
        {
            builder.Services.AddAWSService<IAmazonS3>(builder.Configuration.GetAWSOptions<AmazonS3Config>());
        }

        // Register the RemoteCacheService
        builder.Services.AddSingleton<ICacheService>(sp =>
            new RemoteCacheService(
                setupS3 ? sp.GetRequiredService<IAmazonS3>() : null,
                setupS3 ? builder.GetRequiredConfig<string>("AWS:S3BucketName") : null,
                sp.GetRequiredService<ConnectionMultiplexer>(),
                sp.GetRequiredService<HttpClient>(),
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
        var metricsToken = Environment.GetEnvironmentVariable("WKC_METRICS_BEARER_TOKEN");

        if (metricsToken != null)
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/metrics"))
                {
                    if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader) ||
                        !authHeader.ToString().Equals($"Bearer {metricsToken}"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }

                await next(context);
            });
        }

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