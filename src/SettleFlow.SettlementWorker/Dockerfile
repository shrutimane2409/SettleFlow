FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first so the package layer is cached until a project file changes.
COPY Directory.Packages.props ./
COPY src/SettleFlow.Contracts/SettleFlow.Contracts.csproj src/SettleFlow.Contracts/
COPY src/SettleFlow.Infrastructure/SettleFlow.Infrastructure.csproj src/SettleFlow.Infrastructure/
COPY src/SettleFlow.Processing/SettleFlow.Processing.csproj src/SettleFlow.Processing/
COPY src/SettleFlow.SettlementWorker/SettleFlow.SettlementWorker.csproj src/SettleFlow.SettlementWorker/
RUN dotnet restore src/SettleFlow.SettlementWorker/SettleFlow.SettlementWorker.csproj
COPY src/ src/
RUN dotnet publish src/SettleFlow.SettlementWorker/SettleFlow.SettlementWorker.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SettleFlow.SettlementWorker.dll"]
