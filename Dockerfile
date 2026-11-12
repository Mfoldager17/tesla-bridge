# Built for both amd64 and arm64 (Raspberry Pi). The SDK runs on the build machine's own
# architecture and cross-compiles, so nothing is emulated during the build.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/TeslaBridge/TeslaBridge.csproj src/TeslaBridge/
RUN dotnet restore src/TeslaBridge/TeslaBridge.csproj -a $TARGETARCH
COPY src/ src/
# Set by CI: the commit (shown as "1.2.0-edge (abc1234)") and the channel (stable = a release, edge = main)
ARG GIT_SHA=""
RUN dotnet publish src/TeslaBridge/TeslaBridge.csproj -c Release -a $TARGETARCH --no-restore --self-contained false -o /app \
    -p:SourceRevisionId=$GIT_SHA

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.source="https://github.com/Mfoldager17/tesla-bridge" \
      org.opencontainers.image.description="Self-hosted Tesla bridge: fleet-telemetry → MQTT/Home Assistant, commands via the Fleet API"
ARG CHANNEL=dev
ENV ASPNETCORE_ENVIRONMENT=Production \
    BRIDGE_CHANNEL=$CHANNEL
EXPOSE 8080 8091
WORKDIR /app
COPY --from=build /app .
# Default telemetry fields/intervals and the root certificates the car trusts. Updated with the image.
COPY config/bridge/ /app/defaults/
ENTRYPOINT ["dotnet", "TeslaBridge.dll"]
