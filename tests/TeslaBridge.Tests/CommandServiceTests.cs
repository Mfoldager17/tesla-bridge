using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeslaBridge.Commands;
using TeslaBridge.Options;
using TeslaBridge.State;

namespace TeslaBridge.Tests;

public class CommandServiceTests
{
    private const string Vin = "VIN1";

    private sealed class FakeTransport(string name, int priority, params CommandResult[] results) : ICommandTransport
    {
        private readonly Queue<CommandResult> _results = new(results);
        public string Name => name;
        public int Priority => priority;
        public bool Reachable { get; set; } = true;
        public int Sends { get; private set; }
        public int Wakes { get; private set; }
        public ValueTask<bool> CanReachAsync(string vin, CancellationToken ct) => ValueTask.FromResult(Reachable);
        public Task<CommandResult> SendAsync(string vin, VehicleCommand command, CancellationToken ct)
        {
            Sends++;
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : CommandResult.Ok());
        }
        public Task<CommandResult> WakeAsync(string vin, CancellationToken ct)
        {
            Wakes++;
            return Task.FromResult(CommandResult.Ok());
        }
    }

    private static (CommandService svc, VehicleStateStore store, List<CommandStatus> statuses) Create(params ICommandTransport[] transports)
    {
        var store = new VehicleStateStore();
        var opts = Microsoft.Extensions.Options.Options.Create(new BridgeOptions
        {
            Commands = new CommandOptions { OptimisticTimeoutSeconds = 45, WakeTimeoutSeconds = 5 },
        });
        var svc = new CommandService(new CommandRouter(transports), store, opts, TimeProvider.System,
            NullLogger<CommandService>.Instance)
        { RetryDelay = TimeSpan.FromMilliseconds(10) };
        var statuses = new List<CommandStatus>();
        svc.StatusChanged += statuses.Add;
        return (svc, store, statuses);
    }

    [Fact]
    public async Task Success_sets_optimistic_value_immediately()
    {
        var (svc, store, statuses) = Create(new FakeTransport("cloud", 100));
        store.Apply(Vin, "climate_on", JsonValue.Create(false), DateTimeOffset.UtcNow);

        var result = await svc.ExecuteAsync(Vin, new Cmd.ClimateOn());

        Assert.True(result.IsSuccess);
        Assert.True(store.Get(Vin, "climate_on")!.Value!.GetValue<bool>());
        Assert.Equal([CommandPhase.Pending, CommandPhase.Succeeded], statuses.Select(s => s.Phase));
    }

    [Fact]
    public async Task Sleeping_car_is_woken_and_command_retried()
    {
        var t = new FakeTransport("cloud", 100, CommandResult.Asleep(), CommandResult.Asleep(), CommandResult.Ok());
        var (svc, _, statuses) = Create(t);

        var result = await svc.ExecuteAsync(Vin, new Cmd.Lock());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, t.Wakes);
        Assert.Equal(3, t.Sends);
        Assert.Contains(statuses, s => s.Phase == CommandPhase.Waking);
    }

    [Fact]
    public async Task Failure_rolls_back_optimistic_value()
    {
        var (svc, store, statuses) = Create(new FakeTransport("cloud", 100, CommandResult.Refused("is_charging")));
        store.Apply(Vin, "charge_limit", JsonValue.Create(80.0), DateTimeOffset.UtcNow);

        var result = await svc.ExecuteAsync(Vin, new Cmd.SetChargeLimit(90));

        Assert.Equal(CommandOutcome.Refused, result.Outcome);
        Assert.Equal(80.0, store.Get(Vin, "charge_limit")!.Value!.GetValue<double>());
        Assert.Equal(CommandPhase.Failed, statuses[^1].Phase);
        Assert.Equal("is_charging", statuses[^1].Error);
    }

    [Fact]
    public async Task Router_prefers_lowest_priority_and_falls_back_on_failure()
    {
        // Like Bluetooth: try the local way first, otherwise the cloud.
        var ble = new FakeTransport("ble", 0, CommandResult.Failed("timeout"));
        var cloud = new FakeTransport("cloud", 100);
        var (svc, _, statuses) = Create(cloud, ble);

        var result = await svc.ExecuteAsync(Vin, new Cmd.FlashLights());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, ble.Sends);
        Assert.Equal(1, cloud.Sends);
        Assert.Equal("cloud", statuses[^1].Transport);
    }

    [Fact]
    public async Task Unreachable_transport_is_skipped()
    {
        var ble = new FakeTransport("ble", 0) { Reachable = false };
        var cloud = new FakeTransport("cloud", 100);
        var (svc, _, _) = Create(ble, cloud);

        await svc.ExecuteAsync(Vin, new Cmd.HonkHorn());

        Assert.Equal(0, ble.Sends);
        Assert.Equal(1, cloud.Sends);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"response":{"result":true,"reason":""}}""", CommandOutcome.Success)]
    [InlineData(HttpStatusCode.OK, """{"response":{"result":false,"reason":"already_set"}}""", CommandOutcome.Success)]
    [InlineData(HttpStatusCode.OK, """{"response":{"result":false,"reason":"is_charging"}}""", CommandOutcome.Refused)]
    [InlineData(HttpStatusCode.RequestTimeout, """{"error":"vehicle unavailable: vehicle is offline or asleep"}""", CommandOutcome.VehicleAsleep)]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"invalid bearer token"}""", CommandOutcome.Failed)]
    public void Fleet_api_responses_are_interpreted(HttpStatusCode status, string body, CommandOutcome expected) =>
        Assert.Equal(expected, FleetApiTransport.Interpret(status, JsonNode.Parse(body), body).Outcome);
}

public class CommandServiceExceptionTests
{
    private sealed class ThrowingTransport : ICommandTransport
    {
        public string Name => "cloud";
        public int Priority => 100;
        public ValueTask<bool> CanReachAsync(string vin, CancellationToken ct) => ValueTask.FromResult(true);
        public Task<CommandResult> SendAsync(string vin, VehicleCommand command, CancellationToken ct) =>
            throw new InvalidOperationException("No Tesla tokens");
        public Task<CommandResult> WakeAsync(string vin, CancellationToken ct) => Task.FromResult(CommandResult.Ok());
    }

    [Fact]
    public async Task Exception_in_transport_is_reported_as_failed_and_rolled_back()
    {
        var store = new VehicleStateStore();
        store.Apply("VIN1", "climate_on", System.Text.Json.Nodes.JsonValue.Create(false), DateTimeOffset.UtcNow);
        var svc = new CommandService(new CommandRouter([new ThrowingTransport()]), store,
            Microsoft.Extensions.Options.Options.Create(new BridgeOptions()), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CommandService>.Instance);
        var statuses = new List<CommandStatus>();
        svc.StatusChanged += statuses.Add;

        var r = await svc.ExecuteAsync("VIN1", new Cmd.ClimateOn());

        Assert.Equal(CommandOutcome.Failed, r.Outcome);
        Assert.False(store.Get("VIN1", "climate_on")!.Value!.GetValue<bool>());
        Assert.Equal("No Tesla tokens", statuses[^1].Error);
    }
}
