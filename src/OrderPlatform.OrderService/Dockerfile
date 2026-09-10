# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/OrderPlatform.OrderService/OrderPlatform.OrderService.csproj
RUN dotnet publish src/OrderPlatform.OrderService/OrderPlatform.OrderService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
# Health check için curl gerekiyor (Proje 1'deki grpc_health_probe ile aynı amaç).
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .

HEALTHCHECK --interval=10s --timeout=5s --start-period=10s --retries=5 \
  CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "OrderPlatform.OrderService.dll"]
