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
USER dms

# Template and document blobs need to survive a redeploy — losing the template a controlled
# document was created from is a data-integrity problem, not an inconvenience. On Railway
# that means attaching a Railway Volume to this service and mounting it at /app/storage; see
# RAILWAY_DEPLOY.md. Deliberately NOT declared with a Dockerfile VOLUME instruction here —
# Railway's builder rejects that directive outright ("use Railway Volumes" is the actual
# build error), so the directory below exists as a plain, writable path for the app to use
# whether or not a volume ends up mounted on top of it; only the mount is what makes it
# durable across deploys.

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "--info"]

ENTRYPOINT ["dotnet", "Dms.Api.dll"]
