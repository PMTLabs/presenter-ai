# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy build metadata and every project file first so source changes do not invalidate restore.
COPY Directory.Build.props PresenterAi.slnx ./
COPY src/PresenterAi.Api/PresenterAi.Api.csproj src/PresenterAi.Api/
COPY src/PresenterAi.Application/PresenterAi.Application.csproj src/PresenterAi.Application/
COPY src/PresenterAi.Cli/PresenterAi.Cli.csproj src/PresenterAi.Cli/
COPY src/PresenterAi.Contracts/PresenterAi.Contracts.csproj src/PresenterAi.Contracts/
COPY src/PresenterAi.Domain/PresenterAi.Domain.csproj src/PresenterAi.Domain/
COPY src/PresenterAi.Infrastructure/PresenterAi.Infrastructure.csproj src/PresenterAi.Infrastructure/
COPY tests/PresenterAi.Api.Tests/PresenterAi.Api.Tests.csproj tests/PresenterAi.Api.Tests/
COPY tests/PresenterAi.Application.Tests/PresenterAi.Application.Tests.csproj tests/PresenterAi.Application.Tests/
COPY tests/PresenterAi.Infrastructure.Tests/PresenterAi.Infrastructure.Tests.csproj tests/PresenterAi.Infrastructure.Tests/

RUN dotnet restore src/PresenterAi.Api/PresenterAi.Api.csproj

COPY src/ src/
RUN dotnet publish src/PresenterAi.Api -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# The .NET runtime image provides the non-root app user (APP_UID).
USER $APP_UID

COPY --from=build /app/publish .

EXPOSE 47913
ENTRYPOINT ["dotnet", "PresenterAi.Api.dll"]
