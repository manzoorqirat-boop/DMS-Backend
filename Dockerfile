# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore against the manifests alone first, so a code-only change reuses the cached restore
# layer. Central package management means Directory.Packages.props is part of that input.
COPY Directory.Build.props Directory.Packages.props Dms.sln ./
COPY src/Dms.Domain/Dms.Domain.csproj             src/Dms.Domain/
COPY src/Dms.Application/Dms.Application.csproj   src/Dms.Application/
COPY src/Dms.Infrastructure/Dms.Infrastructure.csproj src/Dms.Infrastructure/
COPY src/Dms.Api/Dms.Api.csproj                   src/Dms.Api/
RUN dotnet restore src/Dms.Api/Dms.Api.csproj

COPY . .
RUN dotnet publish src/Dms.Api/Dms.Api.csproj -c Release -o /app --no-restore

# Run
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

# Non-root. The container writes only to the storage volume, and a document management system
# running as root is an unnecessary blast radius.
RUN adduser --system --group --no-create-home dms \
    && mkdir -p /app/storage \
    && chown -R dms:dms /app/storage

# Template and document blobs need to survive a redeploy — losing the template a controlled
# document was created from is a data-integrity problem, not an inconvenience. On Railway
# that means attaching a Railway Volume to this service and mounting it at /app/storage; see
# RAILWAY_DEPLOY.md. Deliberately not declared as a persistent mount point in this file:
# Railway's builder rejects that declaration outright at build time, so the mount is
# configured entirely on Railway's side instead.

# The chown above fixes ownership of the image's OWN /app/storage — which a mounted volume
# then covers over at runtime, with a root-owned root directory the dms user cannot write to.
# So ownership has to be corrected once more after the mount exists, which only the entrypoint
# can do. Hence: stay root for the entrypoint, fix the mount, then drop to dms via gosu before
# the application itself starts. The app never runs as root.
RUN apt-get update \
    && apt-get install -y --no-install-recommends gosu \
    && rm -rf /var/lib/apt/lists/*

COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "--info"]

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["dotnet", "Dms.Api.dll"]
