# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

MetaMorph is a media converter service for converting images and videos into Explorer-friendly formats (KTX2 and MP4). It's a .NET 9.0 web service that handles media conversion at scale using a queue-based architecture.

## Build and Development Commands

### Building the Solution
```bash
dotnet build                  # Build entire solution
dotnet build MetaMorphAPI     # Build API project only
dotnet build MetaMorphWorker  # Build Worker project only
```

### Running Tests
```bash
dotnet test                                     # Run all tests
dotnet test --filter "FullyQualifiedName~FileTypeTests"  # Run specific test class
dotnet test --filter "Name~ConvertJPEG"       # Run specific test by name
```

### Running the Service Locally

**Option 1: Local Contained Mode (No AWS dependencies)**
```bash
dotnet run --project MetaMorphAPI --launch-profile "Local Contained"
```

**Option 2: Local AWS Mode (with LocalStack)**
```bash
# First start LocalStack and Redis containers
docker-compose up localstack redis -d

# Then run the API
dotnet run --project MetaMorphAPI --launch-profile "Local AWS"
```

**Option 3: Full Docker Compose**
```bash
docker-compose up --build
```

### Docker Commands
```bash
docker-compose up --build              # Build and run all services
docker-compose up localstack redis -d  # Run only infrastructure services
docker-compose logs -f metamorphapi    # View API logs
docker-compose down                    # Stop all services
```

## Architecture

### Service Components

1. **MetaMorphAPI**: Main API service that receives conversion requests
   - Handles incoming requests at `/convert?url=<image_url>`
   - Returns redirect to original or converted file
   - Manages conversion queue and caching

2. **MetaMorphWorker**: Standalone worker for processing conversions (used in distributed mode)
   - Polls SQS queue for conversion jobs
   - Processes conversions independently

3. **ConversionBackgroundService**: Built-in worker when running in local mode
   - Runs within the API process when `MetaMorph:LocalWorker` is true
   - Processes conversions using configurable concurrency

### Key Services and Their Responsibilities

- **ConvertController** (Controllers/ConvertController.cs:14): Entry point for conversion requests, handles redirects and queuing
- **ConverterService** (Services/ConverterService.cs:13): Core conversion logic using ImageMagick and FFmpeg
- **FileAnalyzerService**: Detects file types (static image, motion image, motion video)
- **DownloadService**: Downloads files from URLs with size limits
- **ICacheService**: Interface for cache storage (local filesystem or S3)
  - **LocalCacheService**: Stores converted files on disk
  - **RemoteCacheService**: Stores in S3 with Redis metadata
- **IConversionQueue**: Interface for job queuing
  - **LocalConversionQueue** (Services/Queue/LocalConversionQueue.cs:8): In-memory queue
  - **RemoteConversionQueue** (Services/Queue/RemoteConversionQueue.cs:12): SQS-based queue

### Configuration Modes

The service can run in different modes based on configuration:

1. **Local Contained**: Everything runs locally with file system storage
   - `MetaMorph:LocalCache=true`
   - `MetaMorph:LocalWorker=true`
   - No external dependencies

2. **Local AWS**: Uses LocalStack for S3/SQS, Redis for metadata
   - `MetaMorph:LocalCache=false`
   - `MetaMorph:LocalWorker=true`
   - Requires LocalStack and Redis

3. **Distributed**: Full AWS deployment with separate workers
   - `MetaMorph:LocalCache=false`
   - `MetaMorph:LocalWorker=false`
   - API and Workers run separately

### Conversion Flow

1. Request arrives at `/convert?url=<image_url>&format=<format>&wait=<bool>`
2. URL + format is hashed using SHA256 for cache key
3. Cache check:
   - If cached and not expired → redirect to cached URL
   - If not cached or expired → queue for conversion
4. Wait parameter handling:
   - If `wait=true` → hold request up to 20 seconds for conversion to complete
   - If `wait=false` or not specified → immediately redirect to original
5. Background conversion:
   - Download file (max size configurable)
   - Detect type (static image, animated image, video)
   - Convert based on format parameter:
     - Static images → resize to 1024x1024 max → KTX2 format with:
       - Default: UASTC compression
       - `format=astc`: ASTC compression with 8x8 block size
       - `format=astc_high`: ASTC compression with 4x4 block size
     - Animated images → extract frames → video format:
       - Default/`format=mp4`: MP4 with H.264
       - `format=ogv`: OGV with Theora
     - Videos → transcode with 512px max width:
       - Default/`format=mp4`: MP4 with H.264
       - `format=ogv`: OGV with Theora
6. Store result in cache (S3 or local filesystem)
7. If waiting, redirect to converted file; otherwise future requests redirect to it

### External Dependencies

- **toktx**: Command-line tool for KTX2 conversion (must be installed)
- **FFmpeg**: Video processing (handled by FFMpegCore package)
- **ImageMagick**: Image processing (Magick.NET package)
- **Redis**: Metadata storage in remote cache mode
- **AWS S3**: File storage in remote cache mode
- **AWS SQS**: Job queue in distributed mode
- **LocalStack**: Local AWS emulation for development

### Key Configuration Settings

```json
{
  "MetaMorph": {
    "LocalWorker": false,        // Run worker in API process
    "LocalCache": false,         // Use filesystem instead of S3
    "StartLocalInfra": false,    // Auto-start LocalStack/Redis
    "MaxDownloadFileSizeMB": 50, // Max file size to download
    "ConcurrentConversions": 1,  // Worker concurrency
    "MinMaxAgeMinutes": 5        // Minimum cache duration
  }
}
```