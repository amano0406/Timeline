FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app
COPY worker/ ./

ENTRYPOINT ["dotnet", "Timeline.Worker.dll"]
