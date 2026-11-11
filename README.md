# Tesla Bridge

Selvhostet erstatning for Teslemetry. Bilen streamer direkte til din egen server, og du styrer den gennem Teslas officielle Fleet API, uden mellemmand og uden abonnement.

Data kan komme ud i Home Assistant ad **to veje**. Vælg den, der passer dig:

| | **A) MQTT** | **B) HA-integration** |
|---|---|---|
| Til dig, der … | allerede har en MQTT-broker (Mosquitto, fx til Zigbee2MQTT) | ikke har eller ikke vil have en broker |
| Ekstra software | din broker | intet |
| I Home Assistant | MQTT-integrationen (discovery, sker automatisk) | **Tesla Bridge** fra HACS |
| Andre klienter (ESP32, Node-RED, egne dashboards) | læser og skriver MQTT-topics | bruger HTTP-API'et |
| Entiteter | ~80 under én enhed pr. bil | præcis de samme navne, typer og entity id'er |

> **Om eksemplerne i denne guide**
> Alle konkrete værdier herunder er **eksempler**, som du skal erstatte med dine egne:
>
> | Eksempel | Erstat med |
> |---|---|
> | `bil.eksempel.dk` | dit eget (sub)domæne. Navnet er frit valgt (`car.`, `bil.`, `tlm.` …), det må bare ikke indeholde "tesla" |
> | `Model Y` / `model_y_` | det navn, du giver bilen i `.env` (se nedenfor) |
> | `<server-IP>`, `192.168.0.x` | IP-adressen på din server og din MQTT-broker |
> | `mosquitto` | navnet på *din* Mosquitto-container (`docker ps`) |
> | `~/docker-services/…` | hvor du nu har dine docker-projekter |
> | `npm-<nr>` | certifikatets nummer i din Nginx Proxy Manager |
> | `<VIN>`, `<kode 1>`, `<kode 2>`, `<token>` | dit VIN og dine egne kodeord/token |
>
> Tekst i `<spidse parenteser>` skal altid udskiftes – parenteserne skal ikke med.

Bilen hedder i HA det, du selv skriver i `Bridge__Vehicles__0__Name` i `.env` (fx "Model Y", "Bilen" eller "Elsa"), og navnet bliver til prefixet i alle entity id'er: "Model Y" → `sensor.model_y_battery_level`, "Bilen" → `sensor.bilen_battery_level`. I eksemplerne her hedder bilen "Model Y".

De to veje giver **identiske entiteter** (`sensor.<bil>_battery_level`, `climate.<bil>_climate`, `lock.<bil>_lock` …), fordi integrationen henter de samme definitioner fra bridgen, som MQTT discovery bruger. Du kan skifte vej senere uden at ændre dashboards eller automationer. Navnene følger Teslemetry, så et skifte derfra kræver kun at entity id'et ændres (`sensor.tesla_battery_level` → `sensor.model_y_battery_level`). Vil du helt undgå det, så giv bilen samme navn, som den har i Teslemetry.

Prisen er Teslas egen Fleet API-afregning, som for én bil ligger inden for den gratis månedlige rabat ($10). Bridgen tæller forbruget for dig.

---

## Indhold

1. [Sådan hænger det sammen](#sådan-hænger-det-sammen)
2. [Forudsætninger](#forudsætninger)
3. [Installation](#installation) (trin 1–8, med A og B undervejs)
4. [Skift mellem MQTT og integration](#skift-mellem-mqtt-og-integration)
5. [Entiteter i Home Assistant](#entiteter-i-home-assistant)
6. [Opdatering](#opdatering)
7. [Fejlsøgning](#fejlsøgning)
8. [Dashboardet](#dashboardet)
9. [Felter og intervaller](#felter-og-intervaller)
10. [MQTT-API](#mqtt-api-vej-a)
11. [HTTP-API](#http-api-vej-b-og-alt-andet)
12. [Test lokalt uden bil](#test-lokalt-uden-bil)
13. [Udvikling](#udvikling)

---

## Sådan hænger det sammen

**Vej A – med din egen broker**
```
Bil ──mTLS:4443──▶ fleet-telemetry ──MQTT──▶ din broker ◀──MQTT──▶ tesla-bridge
                                                 │                    │
                                     HA (MQTT discovery),       tesla-proxy ──▶ Tesla Fleet API ──▶ Bil
                                     ESP32, Node-RED …
```

**Vej B – uden broker**
```
Bil ──mTLS:4443──▶ fleet-telemetry ──MQTT──▶ tesla-bridge (indbygget broker, kun internt i Docker)
                                                 │
                              HTTP + live events (:8090)
                                                 │
                                   HA-integrationen "Tesla Bridge"
                                                 │  kommandoer
                                                 ▼
                                   tesla-proxy ──▶ Tesla Fleet API ──▶ Bil
```

| Container | Fra | Opgave |
|---|---|---|
| `fleet-telemetry` | Tesla (officiel) | Modtager bilens stream (mTLS på port 4443) og lægger rå felter på MQTT |
| `tesla-proxy` | Tesla `vehicle-command` | Signerer kommandoer med din apps nøgle |
| `tesla-init` | denne kode | Kører ved hver start og stopper igen: laver nøgler og fleet-telemetrys config ud fra `.env` |
| `tesla-bridge` | denne kode (.NET 10) | Normaliserer data, HA-entiteter, kommandoer, optimistisk tilstand, dashboard, forbrug |

**Hvad bridgen løser i forhold til Tesla Fleet-integrationen:**
- **Ingen "off → on"-hop.** Trykker du "klima on", vises "on" med det samme. Bilens gamle snapshot lige efter opvågning overskriver det ikke. Bekræfter bilen ikke inden for 45 s, rulles der tilbage.
- **Mellemtrin som Teslemetry.** Låse og klapper viser `locking`/`unlocking`/`opening`/`closing`, mens bilen arbejder.
- **Vækker selv bilen.** Sover bilen, vækkes den, og kommandoen sendes igen, så snart den er online.
- **Aldrig "unknown" efter genstart.** Sidst kendte værdier gemmes i `data/state.json`. Når bilen vågner, hentes et snapshot (højst én gang i timen) for felter, den ikke har streamet endnu.
- **Kører uden HA.** Bridgen, dashboardet, login og parring af nøglen virker uden Home Assistant.

---

## Forudsætninger

Fælles for begge veje:

- **En server med Docker**, fx en Raspberry Pi 4/5 (arm64) eller en x86-maskine. Imaget bygges til begge.
- **Et (sub)domæne**, der peger på din offentlige IP, fx `bil.eksempel.dk` (eksempel – navnet er frit valgt).
  - Tesla tillader **ikke** ordet "tesla" i domænet eller appnavnet, så `tesla.ditdomaene.dk` bliver afvist. Ellers kan subdomænet hedde hvad som helst.
  - Hos Cloudflare: **DNS only** (grå sky), ikke proxied. Bilen skal tale direkte med din server.
- **Et gyldigt certifikat** til domænet (Let's Encrypt, fx fra Nginx Proxy Manager).
- **En reverse proxy** (NPM eller lignende), der sender `https://<dit domæne>` videre til `<server-IP>:8091`. Den port svarer *kun* på den offentlige nøgle og login-callback, aldrig på dashboardet.
- **Port forward 4443** (TCP) fra routeren til serveren. Bilen sender data hertil. NPM og dine eksisterende hosts på 443 påvirkes ikke.
  - Cloudflare Tunnel og ngrok (HTTP) virker **ikke**: de afslutter TLS og bryder dermed bilens mTLS. Skal du bruge en tunnel, skal den være rå TCP (fx Tailscale Funnel i TCP-tilstand eller en VPS med WireGuard).
- **En ny app** på [developer.tesla.com](https://developer.tesla.com) (trin 1).

Kun vej A:
- **En MQTT-broker**, som både serveren og HA kan nå. Din eksisterende virker fint.

Kun vej B:
- **HACS** i Home Assistant (eller kopiér mappen manuelt, se trin 8B).

---

## Installation

### 1. Opret appen hos Tesla

På [developer.tesla.com](https://developer.tesla.com) → *Create application*:

| Felt | Værdi |
|---|---|
| Navn | frit valgt, bare *uden* "tesla", fx `Min Bil` |
| Allowed Origin | `https://<dit domæne>` (fx `https://bil.eksempel.dk`) |
| Allowed Redirect URI | `https://<dit domæne>/auth/callback` |
| Scopes | Vehicle Information, Vehicle Location, Vehicle Commands, Vehicle Charging Management |

Notér **Client ID** og **Client Secret**. Du skal *ikke* selv lave nøgler: bridgen laver nøgleparret og viser den offentlige nøgle på `https://<dit domæne>/.well-known/appspecific/com.tesla.3p.public-key.pem`.

### 2. Domæne, certifikat og reverse proxy

1. Lav en DNS-record for dit domæne (fx `bil.eksempel.dk`) → din offentlige IP (DNS only).
2. I NPM: ny proxy host `<dit domæne>` → `http://<server-IP>:8091`, med Let's Encrypt-certifikat og *Force SSL*.
3. Port forward **TCP 4443** fra routeren til serveren.

### 3. Hent compose-filen og lav `.env`

Serveren skal **kun** have to filer. Ingen kildekode, intet `git`. Bridgen hentes færdigbygget fra `ghcr.io`.

```bash
mkdir -p ~/docker-services/tesla-bridge && cd ~/docker-services/tesla-bridge
curl -fsSLO https://raw.githubusercontent.com/Mfoldager17/tesla-bridge/main/docker-compose.yml
curl -fsSL  https://raw.githubusercontent.com/Mfoldager17/tesla-bridge/main/.env.example -o .env
chmod 600 .env
nano .env
```

Udfyld de fælles værdier (alle værdier er eksempler):

```ini
TZ=Europe/Copenhagen
PUID=1000                                  # fra `id`
PGID=1000
Bridge__Telemetry__Hostname=bil.eksempel.dk     # DIT domæne fra trin 2
Bridge__Vehicles__0__Vin=<VIN>               # 17 tegn, står i Tesla-appen under Software
Bridge__Vehicles__0__Name=Model Y           # frit valgt navn → enhed "Model Y", entity id'er model_y_*
Bridge__Tesla__ClientId=<Client ID fra trin 1>
Bridge__Tesla__ClientSecret=<Client Secret fra trin 1>
Bridge__Web__Token=<token>                  # lang tilfældig streng, fx: openssl rand -hex 24
```

Udfyld derefter **enten A eller B**:

<table>
<tr><th>A) MQTT</th><th>B) Integration (ingen broker)</th></tr>
<tr><td>

```ini
Bridge__Mqtt__EmbeddedBroker=false
Bridge__Mqtt__Host=192.168.0.x   # DIN brokers IP
Bridge__Mqtt__Port=1883
Bridge__Mqtt__Username=tesla-bridge
Bridge__Mqtt__Password=<kode 1>
Bridge__Telemetry__MqttUsername=fleet-telemetry
Bridge__Telemetry__MqttPassword=<kode 2>
Bridge__Mqtt__DiscoveryPrefix=homeassistant
```

Brug IP'en, **ikke** `localhost`. Bridgen og fleet-telemetry kører i containere og skal kunne nå brokeren.

</td><td>

```ini
Bridge__Mqtt__EmbeddedBroker=true
Bridge__Mqtt__DiscoveryPrefix=
```

Resten af MQTT-felterne kan stå tomme. Bridgen starter selv en lille broker, som kun fleet-telemetry taler med over docker-netværket. Den er ikke tilgængelig udefra.

</td></tr>
</table>

### 4. MQTT-brugere (kun A)

Bridgen og fleet-telemetry logger ind på din broker med hver sin bruger. Brugernavnene `tesla-bridge` og `fleet-telemetry` er forslag – brug gerne andre, bare de står ens her og i `.env`. Med Mosquitto i en container (udskift `mosquitto` med navnet på din container):

```bash
docker exec -it mosquitto mosquitto_passwd /mosquitto/config/passwd tesla-bridge     # <kode 1>
docker exec -it mosquitto mosquitto_passwd /mosquitto/config/passwd fleet-telemetry  # <kode 2>
docker restart mosquitto
```

- Advarslen om filrettigheder fra `mosquitto_passwd` kan ignoreres.
- Er din broker anonym (`allow_anonymous true`), springer du dette over og lader brugernavne og kodeord stå tomme i `.env`.
- Tjek et login: `docker exec mosquitto mosquitto_sub -u tesla-bridge -P '<kode 1>' -t '$SYS/broker/version' -C 1`. Det skal skrive en version, ikke "not authorised".

### 5. Certifikatet til fleet-telemetry

Bilen taler direkte med fleet-telemetry på port 4443, så den skal have domænets certifikat. Læg `fullchain.pem` og `privkey.pem` i `config/fleet-telemetry/certs/`.

Fra Nginx Proxy Manager. Ret stien til hvor din NPM ligger, og `npm-<nr>` til dit certifikats nummer (se under *SSL Certificates* i NPM, eller `ls <npm-mappe>/letsencrypt/live/`):

```bash
mkdir -p config/fleet-telemetry/certs
NPM=~/docker-services/nginx-proxy-manager/letsencrypt/live/npm-<nr>   # eksempel – ret til din
sudo cp -L $NPM/fullchain.pem $NPM/privkey.pem config/fleet-telemetry/certs/
sudo chown -R $(id -u):$(id -g) config/fleet-telemetry/certs
```

Let's Encrypt fornyer hver ~60. dag, så læg de samme linjer i et cron-job (fx ugentligt), efterfulgt af `docker compose restart fleet-telemetry`.

### 6. Start

```bash
docker compose up -d
docker compose logs tesla-init          # hvad der blev lavet + advarsler
docker compose logs -f tesla-bridge     # A: "Forbundet til MQTT 192.168.0.x"   B: "Indbygget MQTT-broker kører"
```

`tesla-init` kører først ved hver start og stopper igen. Den laver appens nøgle (`config/tesla-proxy/fleet-key.pem`) og den interne TLS, hvis de mangler, og skriver `config/fleet-telemetry/config.json` ud fra `.env`. Eksisterende nøgler røres aldrig.

**Tag `config/` og `data/` med i din backup.** Mister du `fleet-key.pem`, skal nøglen parres med bilen igen.

### 7. Login, domæne og parring i dashboardet

Åbn `http://<server>:8090` (kun på dit lokale net) og log ind med `Bridge__Web__Token`. Panelet **Opsætning** fører dig igennem:

1. **Registrér domænet hos Tesla** (partner-registrering). Kræver at `https://<dit domæne>` virker fra internettet.
2. **Log ind med Tesla**.
3. **Par nøglen med bilen**: scan QR-koden med telefonen (åbner `tesla.com/_ak/<dit domæne>`) og godkend i Tesla-appen, mens du er ved bilen.
4. **Send telemetri-konfigurationen** til bilen. Det sker også automatisk ved første start, og den fornyes selv (den udløber efter 30 dage).

Åbn Tesla-appen for at vække bilen. Data bør dukke op inden for et minut. Tjek status fra terminalen:

```bash
docker compose exec tesla-bridge dotnet TeslaBridge.dll telemetry-status   # vent på "synced": true
docker compose exec tesla-bridge dotnet TeslaBridge.dll telemetry-errors   # hvis der ikke kommer data
```

> Bilen kan streame til flere apps. Bruger du Teslemetry, fortsætter den side om side. Opsig den, når du er tilfreds.

### 8A. Home Assistant via MQTT

Er HA allerede koblet på samme broker (fx til Zigbee2MQTT), dukker enhederne **<dit bilnavn>** og **Tesla Bridge** op af sig selv under MQTT. Ellers: *Indstillinger → Enheder og tjenester → Tilføj integration → MQTT*, og peg den på brokeren.

Kommer der ingen entiteter, så tjek at `Bridge__Mqtt__DiscoveryPrefix=homeassistant`, og at HA's MQTT-integration bruger samme prefix (standard).

### 8B. Home Assistant via integrationen

**Med HACS:**
1. HACS → ⋮ (øverst til højre) → **Custom repositories**
2. Repository: `https://github.com/Mfoldager17/tesla-bridge`, type: **Integration** → *Add*
3. Søg efter **Tesla Bridge** i HACS → *Download*
4. **Genstart Home Assistant**
5. *Indstillinger → Enheder og tjenester → Tilføj integration → Tesla Bridge*
6. URL: `http://<server-IP>:8090`, token: `Bridge__Web__Token` fra `.env`

**Uden HACS:** kopiér mappen `custom_components/tesla_bridge` fra dette repo til HA's `config/custom_components/tesla_bridge`, genstart HA, og fortsæt fra punkt 5.

Integrationen:
- henter entiteterne fra bridgen (`/api/entities`), så nye entiteter i en ny bridge-version kommer med, når integrationen genindlæses
- får ændringer **live** via Server-Sent Events. Den poller ikke, og den indhenter alt, hvad der er sket, hvis forbindelsen har været væk
- viser alle entiteter som *ikke tilgængelig*, hvis bridgen er nede, præcis som MQTT gør
- beder om det nye token (reauth) ved næste genstart, hvis du ændrer `Bridge__Web__Token`

---

## Skift mellem MQTT og integration

Begge veje kan køre **samtidig**, mens du tester. Entiteterne fra den vej, du tilføjer sidst, får så `_2` på deres entity id. Når du vil skifte helt:

### Fra A (MQTT) til B (integration)

1. **Skift `.env`** til B og genstart, så bridgen holder op med at sende til din broker:
   ```ini
   Bridge__Mqtt__EmbeddedBroker=true
   Bridge__Mqtt__DiscoveryPrefix=
   ```
   ```bash
   docker compose up -d
   ```
2. **Ryd bridgens gemte beskeder i din broker.** Brokeren husker discovery-configs og sidste værdier (retained). Bliver de liggende, kan HA genskabe MQTT-enheden ved næste genstart. VIN skal stå med **små bogstaver**:
   ```bash
   docker exec mosquitto mosquitto_sub -u tesla-bridge -P '<kode 1>' \
     -t 'homeassistant/+/tesla_bridge_<vin med små bogstaver>/+/config' \
     -t 'homeassistant/sensor/tesla_bridge/+/config' \
     -t 'tesla/#' \
     --remove-retained --retained-only -W 3
   ```
   Den slutter med "Timed out" efter 3 sekunder. Det er forventet. Når discovery-configs tømmes, fjerner HA selv MQTT-entiteterne. Står der en tom enhed tilbage under MQTT, så slet den.
   Kun bridgens egne topics ryddes. Zigbee2MQTT og alt andet på brokeren røres ikke.
3. **(Valgfrit) Fjern MQTT-brugerne.** De gør ingen skade, hvis de bliver:
   ```bash
   docker exec mosquitto mosquitto_passwd -D /mosquitto/config/passwd tesla-bridge
   docker exec mosquitto mosquitto_passwd -D /mosquitto/config/passwd fleet-telemetry
   docker restart mosquitto
   ```
4. **Tilføj integrationen** (trin 8B). Entiteterne får de samme entity id'er som før, så dashboards og automationer virker uændret.

### Fra B (integration) til A (MQTT)

1. Opret MQTT-brugerne (trin 4).
2. Skift `.env` til A (trin 3), og kør `docker compose up -d`.
3. Slet integrationen **Tesla Bridge** i HA (*Indstillinger → Enheder og tjenester*), så entity id'erne bliver ledige.
4. Genstart bridgen (`docker compose restart tesla-bridge`). Den sender discovery igen, og MQTT-entiteterne dukker op med de samme entity id'er.

---

## Entiteter i Home Assistant

Samme navne og typer som Teslemetry. Prefixet kommer fra `Bridge__Vehicles__0__Name`: "Model Y" giver `model_y_`, "Bilen" giver `bilen_`. Æ, ø og å bliver til ae, oe og a. Tabellen viser navnene uden prefix.

Har du flere biler, så tilføj `Bridge__Vehicles__1__Vin` og `Bridge__Vehicles__1__Name` osv. Hver bil bliver sin egen enhed med sit eget prefix.

| Type | Entiteter |
|---|---|
| `climate` | `climate` (tænd/sluk, temperatur, preset off/keep/dog/camp), `cabin_overheat_protection` (fra/køl/kun blæser, 30/35/40 °C) |
| `lock` | `lock`, `charge_cable_lock` |
| `cover` | `frunk` (kun åbne), `trunk`, `charge_port_door`, `windows` |
| `switch` | `charge`, `sentry_mode`, `defrost`, `guest_mode`, `valet_mode`, `auto_seat_climate_left/right`, `auto_steering_wheel_heater` |
| `number` | `charge_limit`, `charge_current` |
| `select` | `seat_heater_front_left/right`, `seat_heater_rear_left/center/right`, `steering_wheel_heater` |
| `button` | `wake`, `flash_lights`, `honk_horn`, `homelink`, `keyless_driving`, `play_fart` |
| `sensor` | `battery_level`, `charging`, `charger_power/voltage/current`, `charge_energy_added`, `time_to_full_charge`, `energy_remaining`, `estimate_battery_range`, `ideal_battery_range`, `speed`, `shift_state` (P/D/R/N), `odometer`, `tire_pressure_*`, `inside/outside_temperature`, `destination`, `time_to_arrival`, `distance_to_arrival`, `state_of_charge_at_arrival`, `version`, `last_seen`, `command_status` … |
| `binary_sensor` | `status` (online), `charge_cable`, døre, vinduer, `driver_seat_occupied`, `driver_seat_belt`, `preconditioning_enabled`, `scheduled_charging_pending`, `fast_charger_present` |
| `device_tracker` | `location` (bilen), `route` (destination fra navigationen) |

Desuden enheden **Tesla Bridge** med Fleet API-forbruget: `sensor.tesla_bridge_cost_month`, `…_cost_projected`, `…_credit_left` samt antal signaler, kommandoer, wakes og data-forespørgsler for måneden. Velegnet til en automation, der advarer, hvis det forventede forbrug nærmer sig $10.

`sensor.<bil>_command_status` viser den seneste kommando (`pending` → `waking` → `succeeded`/`failed`) med fejlen som attribut.

---

## Opdatering

**Bridgen** (begge veje):
```bash
cd ~/docker-services/tesla-bridge
docker compose pull && docker compose up -d
```
Vil du låse en version, så sæt `BRIDGE_TAG=v1.2.0` i `.env`. Rul tilbage ved at sætte en ældre tag.
Ændringer i selve `docker-compose.yml` (sjældne) hentes med samme `curl` som i trin 3. `.env` overskrives ikke.

**Integrationen** (vej B): HACS viser en opdatering, når der er nye commits. Installér, og genstart HA. Kommer der nye entiteter i bridgen, dukker de op, når integrationen genindlæses (*⋮ → Genindlæs*).

---

## Fejlsøgning

| Symptom | Tjek |
|---|---|
| Ingen data fra bilen | `telemetry-status` skal vise `"synced": true`. Kør `telemetry-errors`. Er port 4443 åben udefra? Ligger certifikatet i `config/fleet-telemetry/certs/`? `docker compose logs fleet-telemetry` |
| A: "not authorised" i loggen | Bruger/kode i `.env` passer ikke med Mosquittos `passwd`. Test med `mosquitto_sub` (trin 4) |
| A: Ingen entiteter i HA | `Bridge__Mqtt__DiscoveryPrefix` skal være `homeassistant`, og HA skal være på samme broker |
| B: "Kan ikke nå bridgen" i HA | Brug serverens IP, ikke `localhost`. Prøv `curl http://<server>:8090/api/info` fra HA-maskinen. `Bridge__Web__Enabled` må ikke være `false` |
| B: "Forkert token" | Tokenet skal være præcis `Bridge__Web__Token` fra `.env` |
| Entiteter hedder `_2` | Begge veje kører samtidig. Se [Skift mellem MQTT og integration](#skift-mellem-mqtt-og-integration) |
| Kommandoer fejler med "Ikke logget ind" | Log ind med Tesla i dashboardet (trin 7) |
| Kommandoer fejler med "key not paired" | Par nøglen igen via QR-koden (trin 7), mens du er ved bilen |
| "Bil: venter på signal" | Bilen sover. Bridgen spørger Tesla ved start. Når bilen vågner, kommer der data |
| Værdier er "unknown" | Bilen sender kun ved ændring. Når den vågner, hentes et snapshot (højst én gang i timen) |

---

## Dashboardet

Bridgen har et indbygget dashboard på `http://<server>:8090` (`http://localhost:8080` ved lokal test). Du kan se, at alt virker, før HA kobles på:

- **Opsætning**: domæne, Tesla-login, parring og telemetri-status, trin for trin
- **Overblik**: batteri, klima, lås, opladning, temperaturer, position. Lilla kant = venter på at bilen bekræfter
- **Kommandoer**: alle knapper, med en log der viser *sendt → vækker bilen → udført/fejlede*
- **Alle felter**: hver værdi med kilde (`stream`, `snapshot`, `optimistic`, `restored`, `bridge`) og alder
- **Rå telemetri**: præcis hvad bilen sendte, før bridgen oversatte det
- **Forbrug**: månedens signaler, kommandoer, wakes og data-forespørgsler med pris efter Teslas takst og fremskrivning til månedens udgang

Dashboardet kan låse bilen op. Sæt derfor altid `Bridge__Web__Token`, og **port-forward aldrig port 8090**. Den offentlige port 8091 svarer kun på den offentlige nøgle og login-callback. `Bridge__Web__Enabled=false` slår dashboardet og HTTP-API'et fra, og så virker vej B ikke.

---

## Felter og intervaller

`config/bridge/telemetry-fields.json` bestemmer, hvilke felter bilen sender, og hvor ofte højst. Bilen sender **kun ved ændring**, så:
- diskrete felter (lås, døre, klima, ladestatus, gear) står på 1 s og føles live uden at koste noget
- hurtigt skiftende felter (position, hastighed, batteri) er dem, der koster, og står på 60–600 s

Standardfilen følger med imaget. Vil du have dine egne intervaller, så læg en kopi i `config/bridge/telemetry-fields.json` på serveren, sæt `Bridge__Telemetry__FieldsFile=/config/bridge/telemetry-fields.json` i `.env`, og genstart. Bridgen opdager selv, at felterne er ændret, og sender den nye konfiguration til bilen.

Felter, bridgen ikke kender, kommer igennem uændret som `snake_case` (fx `TpmsPressureFl` → `tpms_pressure_fl`). Gyldige navne står i Teslas [vehicle_data.proto](https://github.com/teslamotors/fleet-telemetry/blob/main/protos/vehicle_data.proto).

---

## MQTT-API (vej A)

**Tilstand** (retained, JSON-værdier, metriske enheder):
```
tesla/<VIN>/state/battery_level        72.5
tesla/<VIN>/state/climate_on           true
tesla/<VIN>/state/charging_state       "charging"
tesla/<VIN>/state/gear                 "D"
tesla/<VIN>/state/location             {"latitude":56.1,"longitude":10.1}
tesla/<VIN>/state/locked_motion        "LOCKING"   (også *_motion for frunk, trunk, ladeport og vinduer)
tesla/<VIN>/json                       hele snapshot med updated_at og source pr. felt
tesla/<VIN>/command/status             {"command":"ClimateOn","status":"waking",...}
tesla/bridge/usage                     Fleet API-forbrug for måneden
tesla/bridge/status                    online | offline
```
`source` er `stream` (bekræftet af bilen), `snapshot` (hentet fra Fleet API), `optimistic` (venter på bekræftelse), `restored` (fra disk) eller `bridge`.

**Kommandoer**: publicér på `tesla/<VIN>/cmd/<navn>`:

| Navn | Payload |
|---|---|
| `climate`, `defrost`, `charge`, `sentry_mode`, `guest_mode`, `valet_mode`, `steering_wheel_heat`, `auto_seat_climate_left/right`, `auto_steering_wheel_heater` | `on` / `off` |
| `lock` | `LOCK` / `UNLOCK` |
| `trunk`, `windows`, `charge_port_door` | `OPEN` / `CLOSE` |
| `frunk` | `OPEN` |
| `charge_cable_lock` | `UNLOCK` (åbner ladeporten og frigiver kablet) |
| `driver_temp` | `21.5` (15–28) |
| `climate_keeper` | `off` `keep` `dog` `camp` |
| `cabin_overheat_protection` | `off` `on` `fan_only` |
| `cop_temp` | `30` `35` `40` |
| `charge_limit` / `charge_current` | `80` / `16` |
| `seat_heater_left/right`, `seat_heater_rear_left/center/right` | `off` `low` `medium` `high` eller `0`–`3` |
| `steering_wheel_heater` | `off` `low` `high` |
| `wake`, `flash_lights`, `honk_horn`, `homelink`, `keyless_driving`, `play_fart` | hvad som helst |

```bash
mosquitto_pub -u tesla-bridge -P '<kode 1>' -t tesla/<VIN>/cmd/climate -m on
```

---

## HTTP-API (vej B og alt andet)

Det er det API, integrationen og dashboardet bruger. Det kører på port 8090 i begge veje, så ESP32-paneler, Node-RED osv. også kan bruge det uden MQTT.
Token: `Authorization: Bearer <Bridge__Web__Token>` (eller `?token=` i URL'en).

| Endpoint | |
|---|---|
| `GET /api/info` | starttid, biler, om MQTT er forbundet, om token kræves (kræver ikke selv token) |
| `GET /api/entities` | alle HA-entiteter: samme definitioner som MQTT discovery, med `state:<felt>` / `cmd:<navn>` i stedet for topics |
| `GET /api/state` | alle felter pr. bil med værdi, tidspunkt og kilde (inkl. `*_motion`) |
| `GET /api/events` | Server-Sent Events: `field` (en værdi ændrede sig) og `command` (kommandostatus) live |
| `POST /api/vehicles/<VIN>/cmd/<navn>` | kommando. Body = samme payload som på MQTT (tom body = `PRESS`) |
| `GET /api/usage` | Fleet API-forbrug |

```bash
T=<Bridge__Web__Token>
curl -H "Authorization: Bearer $T" http://<server>:8090/api/state
curl -H "Authorization: Bearer $T" -X POST -d on http://<server>:8090/api/vehicles/<VIN>/cmd/climate
curl -N -H "Authorization: Bearer $T" http://<server>:8090/api/events      # live
```

---

## Test lokalt uden bil

Kræver kun [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
cd src/TeslaBridge
dotnet run
```

Åbn **http://localhost:8080**, og tryk **Send testdata**. Profilen "Lokal test" (`appsettings.Development.json`) starter den **indbyggede broker** (som vej B) og en **simulator**, der sender falske telemetri-beskeder gennem MQTT præcis som fleet-telemetry. Kommandoer fejler pænt med "Ikke logget ind hos Tesla".

Du kan også pege en test-HA med integrationen på `http://<din maskine>:8080`.

> Optager noget allerede port 1883, så kør `Bridge__Mqtt__Port=1884 dotnet run`.

---

## Udvikling

```
tesla-bridge/
├── docker-compose.yml              det eneste serveren skal have (+ .env)
├── docker-compose.build.yml        byg bridgen lokalt i stedet for at hente fra ghcr.io
├── Dockerfile                      amd64 + arm64 (krydskompileret)
├── .env.example                    alt dit eget: domæne, app, VIN, MQTT/indbygget broker
├── hacs.json                       gør repoet installerbart fra HACS
├── custom_components/tesla_bridge/ HA-integrationen (vej B)
│   ├── __init__.py                 hub: henter entiteter og tilstand, lytter på live events
│   ├── entity.py                   fælles basis: renderer de samme templates som MQTT
│   ├── config_flow.py              URL + token, reauth
│   └── sensor.py, climate.py …     én fil pr. platform
├── config/bridge/                  bygges ind i imaget (/app/defaults)
│   ├── telemetry-fields.json       felter + intervaller sendt til bilen
│   └── vehicle-trust-ca.pem        rodcertifikater bilen stoler på
├── src/TeslaBridge/
│   ├── Program.cs                  DI + CLI (init, auth, push-config, telemetry-status/-errors)
│   ├── Setup/Bootstrap.cs          nøgler, intern TLS, fleet-telemetrys config (tesla-init)
│   ├── Telemetry/                  SignalMapper (rå → normaliseret), ingest, snapshot, config-push
│   ├── State/                      VehicleStateStore (bekræftet vs. forventet), persistens
│   ├── Commands/                   kommandoer, parser, CommandService (optimistisk + væk + retry)
│   ├── Publishing/                 HomeAssistantDiscovery (entiteterne – bruges af BEGGE veje), StatePublisher
│   ├── Mqtt/                       forbindelse med genopkobling og Last Will, indbygget broker
│   ├── Web/                        dashboard, HTTP-API, live events (SSE)
│   └── Auth/, Usage/               Tesla-login og forbrugstælling
├── tests/TeslaBridge.Tests/        .NET-tests
└── tests/ha/                       integrationen mod en kørende bridge (pytest-homeassistant-custom-component)
```

**Tilføj eller ret en entitet:** gør det i `Publishing/HomeAssistantDiscovery.cs`. Både MQTT discovery og `/api/entities` bygger på den, så integrationen får ændringen automatisk.

**Tests:**
```bash
dotnet test                                              # .NET
(cd src/TeslaBridge && dotnet run &)                     # bridge med simulator på :8080
pip install pytest-homeassistant-custom-component
python -m pytest tests/ha -v                             # integrationen mod bridgen
```

**Udgivelse:** push til `main` → GitHub Actions kører begge testsæt og bygger `ghcr.io/mfoldager17/tesla-bridge:latest` (amd64 + arm64). Et tag som `v1.2.0` giver også `:v1.2.0` og `:v1.2`.

**BLE senere:** lav `BleTransport : ICommandTransport` med `Priority = 0`, og registrér den i `Program.cs`. `CommandRouter` prøver BLE først og falder tilbage til Fleet API.
