FROM node:20-bookworm-slim AS assets
WORKDIR /src/web
COPY web/package.json ./
RUN npm install
COPY web/ ./
RUN npm run build:assets

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src/web
COPY web/ ./
COPY shared/TimelineDashboardStatsService.cs /src/shared/TimelineDashboardStatsService.cs
COPY shared/TimelineStoreExportService.cs /src/shared/TimelineStoreExportService.cs
COPY --from=assets /src/web/wwwroot/vendor ./wwwroot/vendor
RUN dotnet restore Timeline.Web.csproj
RUN dotnet publish Timeline.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "Timeline.Web.dll"]
