using Amazon.S3;
using Amazon.SQS;
using MetaMorphAPI.Services;
using MetaMorphAPI.Services.Cache;
using MetaMorphAPI.Services.Queue;
using Serilog;
using StackExchange.Redis;

namespace MetaMorphAPI.Utils;

public static class BootstrapHelper
{
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
        var tempDirectory = Path.Combine(builder.Environment.ContentRootPath, "temp");
        Directory.CreateDirectory(tempDirectory);

        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<FileAnalyzerService>();
        builder.Services.AddSingleton<ConverterService>(sp =>
            new ConverterService(tempDirectory, sp.GetRequiredService<FileAnalyzerService>(),
                sp.GetRequiredService<ILogger<ConverterService>>()));
        builder.Services.AddSingleton<DownloadService>(sp =>
            new DownloadService(tempDirectory, sp.GetRequiredService<HttpClient>()));

        builder.Services.AddHostedService<ConversionBackgroundService>();
    }

    internal static void SetupLocalCache(this WebApplicationBuilder builder)
    {
        var localConvertedDirectory = Path.Combine(builder.Environment.WebRootPath, "converted");
        Directory.CreateDirectory(localConvertedDirectory);

        builder.Services.AddSingleton<ICacheService, LocalCacheService>(sp =>
            new LocalCacheService(localConvertedDirectory, sp.GetRequiredService<ILogger<LocalCacheService>>()));
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

    public static T GetRequiredConfig<T>(this IHostApplicationBuilder builder, string key)
    {
        var section = builder.Configuration.GetSection(key);
        if (!section.Exists())
            throw new InvalidOperationException($"Missing required configuration: {key}");

        return section.Get<T>()!;
    }
}