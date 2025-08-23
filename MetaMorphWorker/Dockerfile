# Stage 0: Build KTX-Software from source
FROM --platform=$BUILDPLATFORM alpine:latest AS ktx-builder
ARG KTX_VERSION=4.4.0
ARG TARGETARCH
RUN apk add --no-cache bash cmake make g++ git zlib-dev bzip2-dev libstdc++ ninja python3 \
    && git clone --branch v${KTX_VERSION} --depth 1 https://github.com/KhronosGroup/KTX-Software.git /tmp/KTX-Software \
    && cd /tmp/KTX-Software \
    && git submodule update --init --recursive --depth 1 \
    && cmake -G Ninja -S /tmp/KTX-Software -B /tmp/KTX-Software/build \
         -DBUILD_SHARED_LIBS=OFF \
         -DCMAKE_BUILD_TYPE=Release \
         -DCMAKE_INSTALL_PREFIX=/out \
         -DKTX_FEATURE_STATIC_LIBRARY=ON \
         -DKTX_FEATURE_TOOLS=ON \
         -DBASISU_SUPPORT_ASTC=ON \
    && cmake --build /tmp/KTX-Software/build --target install \
    && rm -rf /tmp/KTX-Software

# Stage 1: Base runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS base
ARG TARGETARCH
USER $APP_UID
WORKDIR /app



# Install ffmpeg and required libraries for toktx and ImageMagick
USER root
RUN apk add --no-cache ffmpeg libstdc++ libgomp \
    # ImageMagick dependencies for ARM64
    imagemagick imagemagick-libs imagemagick-dev \
    # Additional libraries that might be needed
    musl-dev libc6-compat

# Copy toktx from builder stage
COPY --from=ktx-builder /out/bin/toktx /usr/local/bin/
# Make sure it's executable
RUN chmod +x /usr/local/bin/toktx
USER $APP_UID

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["MetaMorphAPI/MetaMorphAPI.csproj", "MetaMorphAPI/"]
COPY ["MetaMorphWorker/MetaMorphWorker.csproj", "MetaMorphWorker/"]
RUN dotnet restore "MetaMorphWorker/MetaMorphWorker.csproj"
COPY . .
WORKDIR "/src/MetaMorphWorker"
RUN dotnet build "MetaMorphWorker.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "MetaMorphWorker.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:ErrorOnDuplicatePublishOutputFiles=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MetaMorphWorker.dll"]
