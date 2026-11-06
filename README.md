# Tesla Bridge

Selvhostet erstatning for Teslemetry. Bilen streamer direkte til din egen server, og alt kommer ud som almindelig MQTT, så Home Assistant, dine egne dashboards og ESP32-paneler kan bruge det samme data.

```
Bil ──mTLS:4443──▶ fleet-telemetry ──MQTT──▶ mosquitto ◀──▶ tesla-bridge ◀──▶ HA / HomeDeck / ESP32 …
                     (Tesla, Go)                               │
                                       tesla-proxy (Tesla) ◀───┘──▶ Tesla Fleet API ──▶ Bil
```

| Container | Fra | Opgave |
|---|---|---|
| `fleet-telemetry` | Tesla (officiel) | Modtager bilens stream (mTLS), lægger rå felter på MQTT |
| `tesla-proxy` | Tesla `vehicle-command` | Signerer kommandoer og telemetri-konfiguration med din apps nøgle |
| `mosquitto` | Eclipse | MQTT-broker – **valgfri**, brug hellere din eksisterende (fx Pi'en med Z2M) |
| `tesla-bridge` | **denne kode** (.NET 10) | Normaliserer data, HA discovery, kommandoer, optimistisk tilstand, fornyer konfigurationen |

Prisen er Teslas egen Fleet API-afregning, som for én bil ligger inden for den gratis månedlige rabat.

---

## Test lokalt på din Mac (uden Docker, MQTT eller Tesla)

Kræver kun [.NET 10 SDK](https://dotnet.microsoft.com/download) (`brew install dotnet`).

```bash
cd src/TeslaBridge
dotnet run
```

Åbn **http://localhost:8080**, og tryk **Send testdata**. Profilen "Lokal test" (`appsettings.Development.json`) gør tre ting:
- starter en **indbygget MQTT-broker** på port 1883, så du ikke skal bruge Mosquitto
- slår **simulatoren** til. Den sender falske telemetri-beskeder gennem MQTT præcis som fleet-telemetry, så hele kæden testes
- slår automatisk config-push fra

Kommandoer fejler pænt med "Ikke logget ind hos Tesla", indtil du kører `dotnet run -- auth` og har `tesla-proxy` kørende. Det er netop det, dashboardet skal vise.

> Optager noget allerede port 1883 på din Mac, så sæt `Bridge__Mqtt__Port=1884 dotnet run`.

## Dashboard

Bridgen har et indbygget dashboard: **http://localhost:8080** lokalt og **http://<server>:8090** i Docker (8080 bruges ofte af Z2M). Det kører også i prod; slå det fra med `Bridge__Web__Enabled=false`. Du kan se, at alt virker, før HA kobles på:

- **Overblik**: batteri, klima, lås, opladning, temperaturer, position. Lilla kant betyder "venter på at bilen bekræfter"
- **Kommandoer**: alle knapper, med en log der viser *sendt → vækker bilen → udført/fejlede*
- **Alle felter**: hver værdi med kilde (`stream`, `optimistic`, `restored`, `bridge`) og alder, der tæller live
- **Rå telemetri**: præcis hvad bilen sendte, før bridgen oversatte det
- **Forbrug**: denne måneds signaler, kommandoer, wakes og data-forespørgsler med pris efter Teslas takst, fremskrivning til månedens udgang mod de gratis $10, og tidligere måneder. Gemmes i `data/usage.json` og overlever genstart
- **Telemetri-konfiguration**: hent status (`synced`), fejl, eller send konfigurationen igen

Sæt `Bridge__Web__Token` i `.env`. Dashboardet kan låse bilen op, så **port-forward aldrig dashboard-porten**.

---

## Hvad bridgen løser

- **Ingen "off → on"-hop.** Når du trykker "klima on", vises "on" med det samme. Bilens gamle snapshot lige efter opvågning overskriver det ikke. Bekræfter bilen ikke inden for 45 s, rulles der tilbage.
- **Vækker selv bilen.** Svarer Fleet API "sover", vækkes bilen, og kommandoen gentages, så snart streamen melder den online.
- **Aldrig "unknown" efter genstart.** Sidst kendte værdier gemmes i `data/state.json`.
- **Én enhed i HA** med cirka 60 entiteter via MQTT discovery, uden custom integration.
- **Klar til BLE.** Kommandoer går gennem `ICommandTransport`; en BLE-transport kan tilføjes uden at røre resten.

---

## Opsætning

### Forudsætninger
- En **ny app** på [developer.tesla.com](https://developer.tesla.com) til bridgen. Allowed Origin: `https://tesla.ditdomaene.dk`. Allowed Redirect URI: `https://tesla.ditdomaene.dk/auth/callback`. Bridgen laver selv sit nøglepar, viser den offentlige nøgle, registrerer domænet og parrer med bilen. HA er ikke nødvendig.
- En proxy-host i NPM (eller anden reverse proxy) for `https://tesla.ditdomaene.dk` → `<server>:8091`. Den port svarer **kun** på den offentlige nøgle og login-callback.
- Et (sub)domæne, fx `tesla.ditdomaene.dk`, der peger på din offentlige IP, med et Let's Encrypt-certifikat.
- **Port forward 4443** fra routeren til maskinen. NPM og dine eksisterende hosts på 443 berøres ikke.
  Porten kan være hvilken som helst. Kun klienter med et certifikat udstedt af Tesla kommer igennem TLS-håndtrykket.
  *Tunnel i stedet?* Cloudflare Tunnel og ngrok (HTTP) virker **ikke**, fordi de afslutter TLS og dermed bryder mTLS. Det kræver en tunnel med rå TCP, fx Tailscale Funnel i TCP-tilstand eller en VPS med WireGuard.
- En MQTT-broker. Din eksisterende virker fint (topics: `telemetry/…` og `tesla/…`). Opret brugerne `fleet-telemetry` og `tesla-bridge` i den.

### 1. Engangsopsætning
Hent repoet til serveren. Selve bridgen bygges ikke her, men hentes færdig fra `ghcr.io`:
```bash
git clone https://github.com/Mfoldager17/tesla-bridge.git ~/docker-services/tesla-bridge
cd ~/docker-services/tesla-bridge
./scripts/setup.sh                    # bruger din egen MQTT-broker
./scripts/setup.sh --with-mosquitto   # eller: kør den medfølgende Mosquitto
```
Scriptet laver intern TLS til `tesla-proxy` og `.env` (og MQTT-brugere, hvis du bruger den medfølgende Mosquitto).

Udfyld `.env` (domæne, Tesla-app, VIN, MQTT), og kør `./scripts/render-config.sh`. Det laver `config/fleet-telemetry/config.json` ud fra `.env`, så alt dit eget står ét sted.

Læg derefter disse filer på plads:

| Fil | Indhold |
|---|---|
| `config/tesla-proxy/fleet-key.pem` | Appens **private** partnernøgle |
| `config/fleet-telemetry/certs/fullchain.pem` + `privkey.pem` | Certifikat til `tesla.ditdomaene.dk`. Fra NPM: `<npm>/letsencrypt/live/npm-N/` |
| `.env` | Domæne, client ID/secret, VIN og navn, MQTT-kodeord |

Tilføj redirect URI'en (fx `https://tesla.ditdomaene.dk/auth/callback`) under **Allowed Redirect URIs** i Tesla-appen. Siden behøver ikke findes.

### 2. Start
```bash
docker compose up -d                         # eller: docker compose --profile mosquitto up -d
docker compose logs -f tesla-bridge
```

### Opdatering
```bash
./scripts/update.sh      # git pull + render-config + docker compose pull + up -d
```
Kun koden og skabelonerne skiftes. `.env`, nøgler, certifikater og `data/` er ikke i git og bliver ikke rørt.
Vil du låse en version, så sæt `BRIDGE_TAG=v1.2.0` i `.env`. Rul tilbage ved at sætte en ældre tag og køre `docker compose up -d`.

*Privat repo?* Så skal Pi'en logge ind én gang: lav et GitHub-token med `read:packages` (og `repo` til `git pull`) og kør `echo <token> | docker login ghcr.io -u Mfoldager17 --password-stdin`.
Åbn dashboardet på `http://<server>:8090`. Panelet **Opsætning** fører dig igennem: registrér domænet, log ind med Tesla, scan QR-koden for at parre nøglen med bilen, og se bilen synkronisere.
Bridgen sender selv telemetri-konfigurationen til bilen ved første start og fornyer den automatisk (den udløber efter 30 dage). Tjek status:
```bash
docker compose run --rm tesla-bridge telemetry-status   # vent på "synced": true
docker compose run --rm tesla-bridge telemetry-errors   # hvis der ikke kommer data
```
Åbn Tesla-appen for at vække bilen. Data bør dukke op inden for et minut.

### 4. Home Assistant

Ud over bilen dukker enheden **Tesla Bridge** op med forbruget: `sensor.tesla_bridge_cost_month`, `…_cost_projected`, `…_credit_left` og antal signaler, kommandoer, wakes og data-forespørgsler for måneden. Velegnet til en automation, der advarer, hvis det forventede forbrug nærmer sig $10.

Er HA allerede koblet på samme broker (som til Z2M), dukker enheden **Tesla** op af sig selv. Ellers tilføjes MQTT-integrationen i HA og peges på brokeren.

> Bilen kan kun sende til ét sted ad gangen pr. app. Kører Teslemetry stadig, bliver deres konfiguration erstattet, når denne pushes. Opsig Teslemetry, når data flyder.

---

## Felter og intervaller

`config/bridge/telemetry-fields.json` bestemmer, hvad bilen sender, og hvor ofte højst. Bilen sender **kun ved ændring**, så:
- diskrete felter (lås, døre, klima, ladestatus) står på 1 s og føles live uden at koste noget
- hurtigt skiftende felter (position, hastighed, batteri) er det, der koster, og står på 60-600 s

Efter en ændring: `docker compose run --rm tesla-bridge push-config`.
Felter, bridgen ikke kender, kommer igennem uændret som `snake_case` (fx `TpmsPressureFl` → `tpms_pressure_fl`). Gyldige navne findes i Teslas [vehicle_data.proto](https://github.com/teslamotors/fleet-telemetry/blob/main/protos/vehicle_data.proto).

---

## MQTT-API (til HA, dashboards, ESP32 …)

**Tilstand** (retained, JSON-værdier, metriske enheder):
```
tesla/<VIN>/state/battery_level        72.5
tesla/<VIN>/state/climate_on           true
tesla/<VIN>/state/charging_state       "charging"
tesla/<VIN>/state/location             {"latitude":56.1,"longitude":10.1}
tesla/<VIN>/json                       hele snapshot med updated_at og source pr. felt
tesla/<VIN>/command/status             {"command":"ClimateOn","status":"waking",...}
tesla/bridge/status                    online | offline
```
`source` er `stream` (bekræftet af bilen), `optimistic` (venter på bekræftelse), `restored` (fra disk) eller `bridge`.

**Kommandoer** (`tesla/<VIN>/cmd/<navn>`):

| Navn | Payload |
|---|---|
| `climate`, `defrost`, `steering_wheel_heat`, `sentry`, `charging`, `charge_port` | `on` / `off` |
| `lock` | `LOCK` / `UNLOCK` |
| `driver_temp` | `21.5` |
| `charge_limit` / `charging_amps` | `80` / `16` |
| `seat_heater_left` / `seat_heater_right` | `off` `low` `medium` `high` eller `0`-`3` |
| `wake`, `flash_lights`, `honk_horn`, `frunk`, `trunk`, `vent_windows`, `close_windows`, `charge_port_open`, `charge_port_close` | hvad som helst |

```bash
mosquitto_pub -t tesla/<VIN>/cmd/climate -m on
```

---

## Projektstruktur

```
tesla-bridge/
├── docker-compose.yml              4 containere (bridgen hentes fra ghcr.io)
├── docker-compose.build.yml        byg bridgen lokalt i stedet
├── Dockerfile                      amd64 + arm64 (krydskompileret)
├── .github/workflows/build.yml     test → byg → push til ghcr.io
├── .env.example                    ALT dit eget: domæne, app, VIN, MQTT
├── scripts/
│   ├── setup.sh                    nøgler, intern proxy-TLS, .env
│   ├── render-config.sh            .env → fleet-telemetry/config.json
│   └── update.sh                   hent ny version
├── config/
│   ├── bridge/
│   │   ├── telemetry-fields.json   felter + intervaller sendt til bilen
│   │   └── vehicle-trust-ca.pem    rodcertifikater bilen stoler på (Let's Encrypt m.fl.)
│   ├── fleet-telemetry/config.template.json  Teslas server → MQTT (udfyldes fra .env)
│   ├── mosquitto/mosquitto.conf
│   └── tesla-proxy/                fleet-key.pem + intern TLS
├── src/TeslaBridge/
│   ├── Program.cs                  DI + CLI (auth, push-config, telemetry-status/-errors)
│   ├── Options/                    konfiguration
│   ├── State/                      FieldValue, VehicleStateStore (bekræftet vs. forventet), persistens
│   ├── Telemetry/                  SignalMapper (rå → normaliseret), ingest, config-push/fornyelse
│   ├── Commands/                   VehicleCommand-typer, ICommandTransport, FleetApiTransport,
│   │                               CommandService (optimistisk + væk + retry), parser, MQTT-listener
│   ├── Publishing/                 StatePublisher, HomeAssistantDiscovery
│   ├── Auth/                       TeslaTokenProvider (roterende refresh tokens)
│   ├── Mqtt/                       delt forbindelse med genopkobling og Last Will, valgfri indbygget broker
│   ├── Web/                        dashboard-API, live events (SSE), signaltælling
│   └── wwwroot/index.html          test-dashboardet
└── tests/TeslaBridge.Tests/        76 tests
```

---

## Tilføj BLE senere

1. Lav `BleTransport : ICommandTransport` med `Priority = 0`, og lad `CanReachAsync` returnere, om bilen er inden for rækkevidde.
2. Registrér den i `Program.cs` ved siden af `FleetApiTransport`.

`CommandRouter` prøver så BLE først og falder automatisk tilbage til cloud. Det er dækket af testen `Router_prefers_lowest_priority_and_falls_back_on_failure`.

---

## Udvikling og udgivelse

Push til `main` → GitHub Actions kører testene og bygger `ghcr.io/mfoldager17/tesla-bridge:latest` (amd64 + arm64).
Et tag som `v1.2.0` giver også `:v1.2.0` og `:v1.2`.

```bash
dotnet test
cd src/TeslaBridge && dotnet run            # lokal test, se ovenfor
dotnet run -- help                          # CLI-kommandoer
```
