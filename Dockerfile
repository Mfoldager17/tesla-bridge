# Bygges til både amd64 og arm64 (Pi). SDK'et kører på byggemaskinens egen arkitektur
# og krydskompilerer, så der ikke skal emuleres under build.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/TeslaBridge/TeslaBridge.csproj src/TeslaBridge/
RUN dotnet restore src/TeslaBridge/TeslaBridge.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/TeslaBridge/TeslaBridge.csproj -c Release -a $TARGETARCH --no-restore --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.source="https://github.com/Mfoldager17/tesla-bridge" \
      org.opencontainers.image.description="Selvhostet Tesla-bridge: fleet-telemetry → MQTT, kommandoer via Fleet API"
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080 8091
WORKDIR /app
COPY --from=build /app .
# Standardfelter/intervaller og de rodcertifikater bilen skal stole på. Opdateres med imaget.
COPY config/bridge/ /app/defaults/
ENTRYPOINT ["dotnet", "TeslaBridge.dll"]
