FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src/worker
COPY worker/ ./
RUN dotnet restore Timeline.Worker.csproj
RUN dotnet publish Timeline.Worker.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "Timeline.Worker.dll"]
