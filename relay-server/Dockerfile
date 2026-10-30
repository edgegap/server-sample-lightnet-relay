# Build from the repository root (it needs LightNet/):
#   docker build --platform linux/amd64 -f relay-server/Dockerfile -t lightnet-relay .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY LightNet/Runtime LightNet/Runtime
COPY relay-server/RelayServer.csproj relay-server/RelayServer.cs relay-server/
RUN dotnet publish relay-server/RelayServer.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
ENV RELAY_TCP_PORT=7777 \
    RELAY_UDP_PORT=7777
EXPOSE 7777/tcp 7777/udp
ENTRYPOINT ["dotnet", "lightnet-relay.dll"]
