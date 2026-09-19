FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/TproxyRelay/TproxyRelay.csproj ./TproxyRelay/
RUN dotnet restore TproxyRelay/TproxyRelay.csproj
COPY src/TproxyRelay/ ./TproxyRelay/
RUN dotnet publish TproxyRelay/TproxyRelay.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .
# Unprivileged runtime: the relay parses untrusted network input and must not
# run as root; /data holds the token key volume and needs app ownership.
RUN addgroup -g 10001 -S tproxy && adduser -u 10001 -S -G tproxy tproxy \
    && mkdir -p /data && chown -R tproxy:tproxy /app /data
USER tproxy
ENV TPROXY_LISTEN_PORT=8080 TPROXY_ADMIN_PORT=8081
EXPOSE 8080 8081
ENTRYPOINT ["dotnet", "TproxyRelay.dll"]
