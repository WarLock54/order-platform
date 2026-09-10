# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/OrderPlatform.PaymentService/OrderPlatform.PaymentService.csproj
RUN dotnet publish src/OrderPlatform.PaymentService/OrderPlatform.PaymentService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .

HEALTHCHECK --interval=10s --timeout=5s --start-period=10s --retries=5 \
  CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "OrderPlatform.PaymentService.dll"]
