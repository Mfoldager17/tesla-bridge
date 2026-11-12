namespace TeslaBridge.Settings;

/// <summary>Human-readable description of each telemetry field, for the settings page.</summary>
public sealed record FieldInfo(string Group, string En, string Da, bool Continuous = false);

public static class TelemetryFieldCatalog
{
    /// <summary>Groups in display order.</summary>
    public static readonly string[] Groups = ["driving", "battery", "charging", "climate", "security", "navigation", "tires", "other"];

    /// <summary>
    /// Continuous = changes all the time while driving or charging. Those are the fields that cost money;
    /// everything else only sends when you do something (lock, open a door …), so 1 s is free in practice.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, FieldInfo> Fields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal)
    {
        ["Location"] = new("driving", "Location", "Position", true),
        ["GpsHeading"] = new("driving", "Heading", "Retning", true),
        ["VehicleSpeed"] = new("driving", "Speed", "Hastighed", true),
        ["Gear"] = new("driving", "Gear (P/D/R/N)", "Gear (P/D/R/N)"),
        ["Odometer"] = new("driving", "Odometer", "Kilometertæller", true),
        ["DriverSeatOccupied"] = new("driving", "Driver seat occupied", "Fører i sædet"),
        ["DriverSeatBelt"] = new("driving", "Driver seat belt", "Førers sele"),

        ["BatteryLevel"] = new("battery", "Battery level", "Batteriniveau", true),
        ["EstBatteryRange"] = new("battery", "Estimated range", "Forventet rækkevidde", true),
        ["IdealBatteryRange"] = new("battery", "Ideal range", "Ideel rækkevidde", true),
        ["RatedRange"] = new("battery", "Rated range", "Nominel rækkevidde", true),
        ["EnergyRemaining"] = new("battery", "Energy remaining", "Energi tilbage", true),

        ["DetailedChargeState"] = new("charging", "Charging state", "Ladestatus"),
        ["ChargeLimitSoc"] = new("charging", "Charge limit", "Ladegrænse"),
        ["ChargeCurrentRequest"] = new("charging", "Charge current setting", "Ladestrøm (indstilling)"),
        ["ChargePortDoorOpen"] = new("charging", "Charge port door", "Ladeport"),
        ["ChargePortLatch"] = new("charging", "Charge cable lock", "Ladekabel-lås"),
        ["ACChargingPower"] = new("charging", "AC charging power", "AC-ladeeffekt", true),
        ["DCChargingPower"] = new("charging", "DC charging power", "DC-ladeeffekt", true),
        ["ACChargingEnergyIn"] = new("charging", "AC energy added", "AC-energi tilført", true),
        ["DCChargingEnergyIn"] = new("charging", "DC energy added", "DC-energi tilført", true),
        ["ChargeAmps"] = new("charging", "Charger current", "Ladestrøm (målt)", true),
        ["ChargerVoltage"] = new("charging", "Charger voltage", "Ladespænding", true),
        ["TimeToFullCharge"] = new("charging", "Time to full charge", "Tid til fuldt opladet", true),
        ["ScheduledChargingPending"] = new("charging", "Scheduled charging pending", "Planlagt opladning venter"),
        ["FastChargerPresent"] = new("charging", "Fast charger connected", "Lynlader tilsluttet"),

        ["HvacPower"] = new("climate", "Climate on/off", "Klima til/fra"),
        ["HvacLeftTemperatureRequest"] = new("climate", "Driver temperature setting", "Førers temperatur"),
        ["HvacRightTemperatureRequest"] = new("climate", "Passenger temperature setting", "Passagers temperatur"),
        ["InsideTemp"] = new("climate", "Inside temperature", "Indetemperatur", true),
        ["OutsideTemp"] = new("climate", "Outside temperature", "Udetemperatur", true),
        ["DefrostMode"] = new("climate", "Defrost", "Afrimning"),
        ["ClimateKeeperMode"] = new("climate", "Keep / Dog / Camp mode", "Keep / Dog / Camp"),
        ["CabinOverheatProtectionMode"] = new("climate", "Cabin overheat protection", "Overophedningsbeskyttelse"),
        ["CabinOverheatProtectionTemperatureLimit"] = new("climate", "Overheat protection limit", "Overophedningsgrænse"),
        ["PreconditioningEnabled"] = new("climate", "Preconditioning", "Forvarmning"),
        ["SeatHeaterLeft"] = new("climate", "Seat heater front left", "Sædevarme foran venstre"),
        ["SeatHeaterRight"] = new("climate", "Seat heater front right", "Sædevarme foran højre"),
        ["SeatHeaterRearLeft"] = new("climate", "Seat heater rear left", "Sædevarme bag venstre"),
        ["SeatHeaterRearCenter"] = new("climate", "Seat heater rear center", "Sædevarme bag midt"),
        ["SeatHeaterRearRight"] = new("climate", "Seat heater rear right", "Sædevarme bag højre"),
        ["AutoSeatClimateLeft"] = new("climate", "Auto seat climate left", "Auto sædeklima venstre"),
        ["AutoSeatClimateRight"] = new("climate", "Auto seat climate right", "Auto sædeklima højre"),
        ["HvacSteeringWheelHeatLevel"] = new("climate", "Steering wheel heater", "Rattvarme"),
        ["HvacSteeringWheelHeatAuto"] = new("climate", "Auto steering wheel heater", "Auto rattvarme"),

        ["Locked"] = new("security", "Lock", "Lås"),
        ["DoorState"] = new("security", "Doors, frunk and trunk", "Døre, frunk og bagklap"),
        ["FdWindow"] = new("security", "Window front driver", "Rude fører foran"),
        ["FpWindow"] = new("security", "Window front passenger", "Rude passager foran"),
        ["RdWindow"] = new("security", "Window rear driver", "Rude fører bag"),
        ["RpWindow"] = new("security", "Window rear passenger", "Rude passager bag"),
        ["SentryMode"] = new("security", "Sentry mode", "Sentry"),
        ["GuestModeEnabled"] = new("security", "Guest mode", "Gæstetilstand"),
        ["ValetModeEnabled"] = new("security", "Valet mode", "Valet-tilstand"),

        ["DestinationName"] = new("navigation", "Destination", "Destination"),
        ["DestinationLocation"] = new("navigation", "Destination location", "Destinationens position"),
        ["MinutesToArrival"] = new("navigation", "Time to arrival", "Tid til ankomst", true),
        ["MilesToArrival"] = new("navigation", "Distance to arrival", "Afstand til ankomst", true),
        ["ExpectedEnergyPercentAtTripArrival"] = new("navigation", "Battery at arrival", "Batteri ved ankomst", true),

        ["TpmsPressureFl"] = new("tires", "Tire pressure front left", "Dæktryk foran venstre", true),
        ["TpmsPressureFr"] = new("tires", "Tire pressure front right", "Dæktryk foran højre", true),
        ["TpmsPressureRl"] = new("tires", "Tire pressure rear left", "Dæktryk bag venstre", true),
        ["TpmsPressureRr"] = new("tires", "Tire pressure rear right", "Dæktryk bag højre", true),

        ["Version"] = new("other", "Software version", "Softwareversion"),
    };

    public static FieldInfo Describe(string field) =>
        Fields.TryGetValue(field, out var f) ? f : new FieldInfo("other", field, field);
}
