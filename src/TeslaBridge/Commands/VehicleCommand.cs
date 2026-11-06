using System.Text.Json.Nodes;

namespace TeslaBridge.Commands;

/// <summary>Hvad vi forventer at se i tilstanden, når en kommando er udført.</summary>
public sealed record Expectation(string Key, JsonNode? Value, Func<JsonNode?, bool>? ConfirmedBy = null);

/// <summary>
/// En kommando i bridgens eget sprog. Transporter (Fleet API i dag, BLE senere)
/// oversætter selv til deres protokol – resten af koden kender kun disse typer.
/// </summary>
public abstract record VehicleCommand
{
    /// <summary>Navn på Fleet API-endpointet (…/command/{FleetEndpoint}).</summary>
    public abstract string FleetEndpoint { get; }

    /// <summary>JSON-body til Fleet API, eller null for tom body.</summary>
    public virtual JsonObject? FleetBody => null;

    /// <summary>Optimistiske værdier, der sættes med det samme.</summary>
    public virtual IReadOnlyList<Expectation> Expectations => [];

    /// <summary>Kort navn til logs og status (fx "ClimateOn").</summary>
    public string Name => GetType().Name;

    public sealed override string ToString() => Name;
}

/// <summary>Teslas seat_position-numre.</summary>
public enum Seat { FrontLeft = 0, FrontRight = 1, RearLeft = 2, RearCenter = 4, RearRight = 5 }

public static class Cmd
{
    private static JsonNode? B(bool b) => JsonValue.Create(b);
    private static JsonNode? N(double d) => JsonValue.Create(d);

    private static readonly string[] ChargingActive = ["starting", "charging"];
    private static readonly string[] ChargingInactive = ["stopped", "complete", "disconnected", "no_power"];

    private static Func<JsonNode?, bool> OneOf(string[] states) =>
        v => v is JsonValue jv && jv.TryGetValue(out string? s) && states.Contains(s);

    private static Func<JsonNode?, bool> Near(double target, double tolerance) =>
        v => v is JsonValue jv && jv.TryGetValue(out double d) && Math.Abs(d - target) <= tolerance;

    public sealed record ClimateOn : VehicleCommand
    {
        public override string FleetEndpoint => "auto_conditioning_start";
        public override IReadOnlyList<Expectation> Expectations => [new("climate_on", B(true))];
    }

    public sealed record ClimateOff : VehicleCommand
    {
        public override string FleetEndpoint => "auto_conditioning_stop";
        public override IReadOnlyList<Expectation> Expectations => [new("climate_on", B(false))];
    }

    public sealed record SetDriverTemp(double Celsius) : VehicleCommand
    {
        public override string FleetEndpoint => "set_temps";
        public override JsonObject FleetBody => new() { ["driver_temp"] = Celsius, ["passenger_temp"] = Celsius };
        public override IReadOnlyList<Expectation> Expectations =>
            [new("driver_temp_setting", N(Celsius), Near(Celsius, 0.25))];
    }

    public sealed record SetDefrost(bool On) : VehicleCommand
    {
        public override string FleetEndpoint => "set_preconditioning_max";
        public override JsonObject FleetBody => new() { ["on"] = On, ["manual_override"] = true };
        public override IReadOnlyList<Expectation> Expectations => [new("defrost", B(On))];
    }

    public sealed record SetSeatHeater(Seat Seat, int Level) : VehicleCommand
    {
        public override string FleetEndpoint => "remote_seat_heater_request";
        public override JsonObject FleetBody => new() { ["seat_position"] = (int)Seat, ["level"] = Level };
        public override IReadOnlyList<Expectation> Expectations =>
            [new(StateKey(Seat), N(Level), Near(Level, 0.1))];

        public static string StateKey(Seat seat) => seat switch
        {
            Seat.FrontLeft => "seat_heater_left",
            Seat.FrontRight => "seat_heater_right",
            Seat.RearLeft => "seat_heater_rear_left",
            Seat.RearCenter => "seat_heater_rear_center",
            _ => "seat_heater_rear_right",
        };
    }

    /// <summary>Klimatilstand: 0 = fra, 1 = hold, 2 = hund, 3 = camp.</summary>
    public sealed record SetClimateKeeper(int Mode) : VehicleCommand
    {
        public static readonly string[] Names = ["off", "keep", "dog", "camp"];
        public override string FleetEndpoint => "set_climate_keeper_mode";
        public override JsonObject FleetBody => new() { ["climate_keeper_mode"] = Mode };
        public override IReadOnlyList<Expectation> Expectations =>
            Mode == 0
                ? [new("climate_keeper_mode", JsonValue.Create("off"))]
                : [new("climate_keeper_mode", JsonValue.Create(Names[Mode])), new("climate_on", B(true))];
    }

    /// <summary>Beskyttelse mod overophedning af kabinen: off / on / fan_only.</summary>
    public sealed record SetCabinOverheatProtection(string Mode) : VehicleCommand
    {
        public override string FleetEndpoint => "set_cabin_overheat_protection";
        public override JsonObject FleetBody => new() { ["on"] = Mode != "off", ["fan_only"] = Mode == "fan_only" };
        public override IReadOnlyList<Expectation> Expectations => [new("cabin_overheat_protection", JsonValue.Create(Mode))];
    }

    public sealed record SetSteeringWheelHeat(bool On) : VehicleCommand
    {
        public override string FleetEndpoint => "remote_steering_wheel_heater_request";
        public override JsonObject FleetBody => new() { ["on"] = On };
        public override IReadOnlyList<Expectation> Expectations => [new("steering_wheel_heat", B(On))];
    }

    public sealed record Lock : VehicleCommand
    {
        public override string FleetEndpoint => "door_lock";
        public override IReadOnlyList<Expectation> Expectations => [new("locked", B(true))];
    }

    public sealed record Unlock : VehicleCommand
    {
        public override string FleetEndpoint => "door_unlock";
        public override IReadOnlyList<Expectation> Expectations => [new("locked", B(false))];
    }

    public sealed record SetSentry(bool On) : VehicleCommand
    {
        public override string FleetEndpoint => "set_sentry_mode";
        public override JsonObject FleetBody => new() { ["on"] = On };
        public override IReadOnlyList<Expectation> Expectations => [new("sentry_mode", B(On))];
    }

    public sealed record StartCharging : VehicleCommand
    {
        public override string FleetEndpoint => "charge_start";
        public override IReadOnlyList<Expectation> Expectations =>
            [new("charging_state", JsonValue.Create("starting"), OneOf(ChargingActive))];
    }

    public sealed record StopCharging : VehicleCommand
    {
        public override string FleetEndpoint => "charge_stop";
        public override IReadOnlyList<Expectation> Expectations =>
            [new("charging_state", JsonValue.Create("stopped"), OneOf(ChargingInactive))];
    }

    public sealed record SetChargeLimit(int Percent) : VehicleCommand
    {
        public override string FleetEndpoint => "set_charge_limit";
        public override JsonObject FleetBody => new() { ["percent"] = Percent };
        public override IReadOnlyList<Expectation> Expectations => [new("charge_limit", N(Percent), Near(Percent, 0.5))];
    }

    public sealed record SetChargingAmps(int Amps) : VehicleCommand
    {
        public override string FleetEndpoint => "set_charging_amps";
        public override JsonObject FleetBody => new() { ["charging_amps"] = Amps };
        public override IReadOnlyList<Expectation> Expectations =>
            [new("charge_current_request", N(Amps), Near(Amps, 0.5))];
    }

    public sealed record OpenChargePort : VehicleCommand
    {
        public override string FleetEndpoint => "charge_port_door_open";
        public override IReadOnlyList<Expectation> Expectations => [new("charge_port_open", B(true))];
    }

    public sealed record CloseChargePort : VehicleCommand
    {
        public override string FleetEndpoint => "charge_port_door_close";
        public override IReadOnlyList<Expectation> Expectations => [new("charge_port_open", B(false))];
    }

    public sealed record OpenFrunk : VehicleCommand
    {
        public override string FleetEndpoint => "actuate_trunk";
        public override JsonObject FleetBody => new() { ["which_trunk"] = "front" };
        public override IReadOnlyList<Expectation> Expectations => [new("frunk_open", B(true))];
    }

    public sealed record ToggleTrunk : VehicleCommand
    {
        public override string FleetEndpoint => "actuate_trunk";
        public override JsonObject FleetBody => new() { ["which_trunk"] = "rear" };
    }

    public sealed record VentWindows(double Lat, double Lon) : VehicleCommand
    {
        public override string FleetEndpoint => "window_control";
        public override JsonObject FleetBody => new() { ["command"] = "vent", ["lat"] = Lat, ["lon"] = Lon };
    }

    public sealed record CloseWindows(double Lat, double Lon) : VehicleCommand
    {
        public override string FleetEndpoint => "window_control";
        public override JsonObject FleetBody => new() { ["command"] = "close", ["lat"] = Lat, ["lon"] = Lon };
    }

    public sealed record FlashLights : VehicleCommand
    {
        public override string FleetEndpoint => "flash_lights";
    }

    public sealed record HonkHorn : VehicleCommand
    {
        public override string FleetEndpoint => "honk_horn";
    }

    /// <summary>Vækker bare bilen. Håndteres specielt af CommandService.</summary>
    public sealed record Wake : VehicleCommand
    {
        public override string FleetEndpoint => "wake_up";
    }
}
