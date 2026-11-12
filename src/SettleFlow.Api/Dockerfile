FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
# Restore first so the package layer is cached until a project file changes.
COPY Directory.Packages.props ./
COPY src/SettleFlow.Contracts/SettleFlow.Contracts.csproj src/SettleFlow.Contracts/
COPY src/SettleFlow.Infrastructure/SettleFlow.Infrastructure.csproj src/SettleFlow.Infrastructure/
COPY src/SettleFlow.Processing/SettleFlow.Processing.csproj src/SettleFlow.Processing/
COPY src/SettleFlow.Api/SettleFlow.Api.csproj src/SettleFlow.Api/
RUN dotnet restore src/SettleFlow.Api/SettleFlow.Api.csproj
COPY src/ src/
RUN dotnet publish src/SettleFlow.Api/SettleFlow.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SettleFlow.Api.dll"]
