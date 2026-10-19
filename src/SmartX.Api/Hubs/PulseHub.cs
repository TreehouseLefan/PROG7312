using Microsoft.AspNetCore.SignalR;

namespace SmartX.Api.Hubs;

/// <summary>
/// Real time channel shared by every dashboard module. Server → client messages:
/// <list type="bullet">
/// <item><c>pulse</c>: Pulse Grid tile changes (1 s).</item>
/// <item><c>stream</c>: processed telemetry packets and queue statistics (500 ms).</item>
/// <item><c>alert</c>: a new alert, pushed the moment it is raised.</item>
/// <item><c>alerts</c>: the full alert board when it changes.</item>
/// <item><c>commands</c>: command history and device shadows when they change.</item>
/// <item><c>engine</c>: suggested actions and automated insights when they change.</item>
/// <item><c>history</c>: the latest history events when new ones are recorded.</item>
/// <item><c>incident</c>: an incident acknowledgement (Part 1).</item>
/// </list>
/// </summary>
public sealed class PulseHub : Hub
{
    public const string Route = "/hubs/pulse";
    public const string PulseMethod = "pulse";
    public const string IncidentMethod = "incident";
    public const string StreamMethod = "stream";
    public const string AlertMethod = "alert";
    public const string AlertsMethod = "alerts";
    public const string CommandsMethod = "commands";
    public const string EngineMethod = "engine";
    public const string HistoryMethod = "history";

    public Task<string> Ping() => Task.FromResult("pong");
}
