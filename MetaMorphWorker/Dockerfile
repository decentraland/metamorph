FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app

# Download and install precompiled KTX-Software
# Install dependencies for downloading and extracting KTX
USER root
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl bzip2 ffmpeg && \
    rm -rf /var/lib/apt/lists/*

# Download and install precompiled KTX-Software
RUN curl -L https://github.com/KhronosGroup/KTX-Software/releases/download/v4.4.0/KTX-Software-4.4.0-Linux-x86_64.tar.bz2 -o /tmp/ktx.tar.bz2 && \
    tar xf /tmp/ktx.tar.bz2 -C /tmp && \
    cp /tmp/KTX-Software-4.4.0-Linux-x86_64/bin/toktx /usr/local/bin/ && \
    cp /tmp/KTX-Software-4.4.0-Linux-x86_64/bin/ktx2ktx2 /usr/local/bin/ && \
    cp /tmp/KTX-Software-4.4.0-Linux-x86_64/bin/ktxinfo /usr/local/bin/ && \
    cp /tmp/KTX-Software-4.4.0-Linux-x86_64/lib/libktx.so* /usr/local/lib/ && \
    rm -rf /tmp/KTX-Software*
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
ENV CACHE_MODE=Remote
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MetaMorphWorker.dll"]
