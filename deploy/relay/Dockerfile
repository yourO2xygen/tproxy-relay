FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/TproxyRelay/TproxyRelay.csproj ./TproxyRelay/
RUN dotnet restore TproxyRelay/TproxyRelay.csproj
COPY src/TproxyRelay/ ./TproxyRelay/
RUN dotnet publish TproxyRelay/TproxyRelay.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .
ENV TPROXY_LISTEN_PORT=8080 TPROXY_ADMIN_PORT=8081
EXPOSE 8080 8081
ENTRYPOINT ["dotnet", "TproxyRelay.dll"]
