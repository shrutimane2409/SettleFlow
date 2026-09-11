FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/SettleFlow.NotificationWorker/SettleFlow.NotificationWorker.csproj
RUN dotnet publish src/SettleFlow.NotificationWorker/SettleFlow.NotificationWorker.csproj -c Release -o /app/publish /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet","SettleFlow.NotificationWorker.dll"]
