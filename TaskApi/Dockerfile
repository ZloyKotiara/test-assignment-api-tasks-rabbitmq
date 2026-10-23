FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY TaskApi/TaskApi.csproj TaskApi/
RUN dotnet restore TaskApi/TaskApi.csproj
COPY TaskApi/ TaskApi/
RUN dotnet publish TaskApi/TaskApi.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "TaskApi.dll"]
