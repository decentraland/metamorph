# MetaMorph

A media converter service for converting images and videos into Explorer friendly formats (KTX2 and MP4).

## Running
You can run the service locally without any additional setup by selecting the "MetaMorphAPI: Local Contained" configuration.

If you want to run it with AWS you need to have LocalStack and Docker (+ docker-compose) installed, then you have two options:

### Docker Compose
To run in docker you can run the following command:

`docker-compose up --build`

This will start all the necessary services (Localstack and Redis), and separate API and Worker services.

### Local AWS
To run from your IDE you can use the "MetaMorphAPI: Local AWS" configuration. This will start LocalStack and a new Redis container, and allow you to run from your IDE. You do not need to run MetaMorphWorker separately as the API service will be running it's own worker background service.

## API Usage

The service exposes a single endpoint for media conversion:

```
GET /convert?url=<media_url>&format=<format>&wait=<bool>
```

### Parameters

- **url** (required): The URL of the media file to convert
- **format** (optional): The desired output format
  - For images: `astc` (8x8 block size), `astc_high` (4x4 block size)
  - For videos: `mp4` (default), `ogv`
  - If not specified, defaults to standard conversion (KTX2 for images, MP4 for videos)
- **wait** (optional): If `true`, holds the request until conversion completes (max 20 seconds)
  - On success: redirects to the converted file
  - On timeout/failure: redirects to the original URL

### Examples

```bash
# Convert image to KTX2 with ASTC compression
curl -L "http://localhost:5133/convert?url=https://example.com/image.jpg&format=astc"

# Convert video to OGV format
curl -L "http://localhost:5133/convert?url=https://example.com/video.mp4&format=ogv"

# Convert and wait for completion
curl -L "http://localhost:5133/convert?url=https://example.com/image.png&format=astc_high&wait=true"
```

## Supported Formats

### Input
- **Images**: JPEG, PNG, GIF, WebP, and other formats supported by ImageMagick
- **Videos**: MP4, MOV, AVI, and other formats supported by FFmpeg

### Output
- **Images**: KTX2 container with:
  - UASTC compression (default)
  - ASTC 8x8 compression (`format=astc`)
  - ASTC 4x4 compression (`format=astc_high`)
- **Videos**: 
  - MP4 with H.264 encoding (default)
  - OGV with Theora encoding (`format=ogv`)