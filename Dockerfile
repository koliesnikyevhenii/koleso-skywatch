# Skywatch Worker (plan P7). Build from the repository root: docker build -t skywatch .
# Runtime data (session, update state, database) lives in /app/data: mount a named volume there.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# global.json is left out on purpose: it pins the local SDK and the test runner; the image brings its own 10.0 SDK.
COPY nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Skywatch.Core/Skywatch.Core.csproj src/Skywatch.Core/
COPY src/Skywatch.Worker/Skywatch.Worker.csproj src/Skywatch.Worker/
RUN dotnet restore src/Skywatch.Worker/Skywatch.Worker.csproj

COPY src/ src/
RUN dotnet publish src/Skywatch.Worker/Skywatch.Worker.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final

# The zone database for Europe/Kyiv (the TZ variable is not needed). Installed explicitly so the image does not
# depend on whether the base image ships it.
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Owned by the non-root app user, so a new named volume mounted here inherits the ownership.
RUN mkdir -p /app/data && chown "$APP_UID:$APP_UID" /app/data
USER $APP_UID

# N6: JSON console logs in the container; rotation is configured on Docker's json-file driver (deploy/docker-compose.yml).
ENV Logging__Console__FormatterName=json \
    Logging__Console__FormatterOptions__TimestampFormat="yyyy-MM-ddTHH:mm:ss.fffZ" \
    Logging__Console__FormatterOptions__UseUtcTimestamp=true

ENTRYPOINT ["dotnet", "Skywatch.Worker.dll"]
