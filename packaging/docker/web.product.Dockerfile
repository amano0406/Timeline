FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app
COPY web/ ./

ENTRYPOINT ["dotnet", "Timeline.Web.dll"]
