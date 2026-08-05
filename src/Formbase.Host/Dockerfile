# Build context is the repository root: the host is one project among several, and restore needs the
# solution-wide props before it can resolve any of them.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# README.md travels with them: Directory.Build.props includes the repo-root readme as a packaging
# item for every project in the tree, so its absence is a missing item rather than a skipped one.
COPY Directory.Build.props Directory.Packages.props nuget.config README.md ./

# Project files first, so a source-only change does not invalidate the restore layer.
COPY src/Formbase.Core/Formbase.Core.csproj src/Formbase.Core/
COPY src/Formbase.DependencyInjection/Formbase.DependencyInjection.csproj src/Formbase.DependencyInjection/
COPY src/Formbase.MorphDb/Formbase.MorphDb.csproj src/Formbase.MorphDb/
COPY src/Formbase.Postgres/Formbase.Postgres.csproj src/Formbase.Postgres/
COPY src/Formbase.SchemaIntelligence/Formbase.SchemaIntelligence.csproj src/Formbase.SchemaIntelligence/
COPY src/Formbase.Host/Formbase.Host.csproj src/Formbase.Host/

RUN dotnet restore src/Formbase.Host/Formbase.Host.csproj

COPY src/ src/

# Publish stage
FROM build AS publish
RUN dotnet publish src/Formbase.Host/Formbase.Host.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=publish /app/publish .

# Switch to built-in non-root user
USER $APP_UID

EXPOSE 8080

# No HEALTHCHECK: this image has neither curl nor wget, and the host has no self-probe mode to
# invoke instead. A healthcheck that assumes a package the base image does not carry reports
# unhealthy for a reason that has nothing to do with the service. Readiness is observed from
# outside, over the published port, until the host grows a probe of its own.

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Formbase.Host.dll"]
