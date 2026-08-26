# Build context is expected to be the repo root (same convention as
# frontend/Dockerfile using ./frontend) so the project can restore its
# ProjectReference to Monitor.Core:
#   docker build -f src/Monitor.Gateway/Dockerfile -t monitor-gateway .
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src

COPY src/Monitor.Core/Monitor.Core.csproj src/Monitor.Core/
COPY src/Monitor.Gateway/Monitor.Gateway.csproj src/Monitor.Gateway/
RUN dotnet restore src/Monitor.Gateway/Monitor.Gateway.csproj

COPY src/Monitor.Core/ src/Monitor.Core/
COPY src/Monitor.Gateway/ src/Monitor.Gateway/
RUN dotnet publish src/Monitor.Gateway/Monitor.Gateway.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS production
WORKDIR /app
COPY --from=build /app .

EXPOSE 8080
ENTRYPOINT ["dotnet", "Monitor.Gateway.dll"]
