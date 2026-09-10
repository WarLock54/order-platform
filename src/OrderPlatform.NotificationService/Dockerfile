# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/OrderPlatform.NotificationService/OrderPlatform.NotificationService.csproj
RUN dotnet publish src/OrderPlatform.NotificationService/OrderPlatform.NotificationService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "OrderPlatform.NotificationService.dll"]
