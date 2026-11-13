namespace TeslaBridge.State;

/// <summary>State keys the bridge sets itself (the car's own fields come from Telemetry/SignalMapper.cs).</summary>
public static class StateKeys
{
    /// <summary>true while the car is awake and connected (from the telemetry stream, Tesla or Bluetooth).</summary>
    public const string Online = "online";

    /// <summary>When the bridge last heard from the car (ISO 8601).</summary>
    public const string LastSeen = "last_seen";

    /// <summary>What the bridge is waiting for the car to do: { waking, keys[] }.</summary>
    public const string Pending = "pending";
}
