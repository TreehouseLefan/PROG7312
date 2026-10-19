using System.Globalization;
using SmartX.Api.Background;
using SmartX.Api.Endpoints;
using SmartX.Api.Hubs;
using SmartX.Api.Seeding;
using SmartX.Api.Services;
using SmartX.Api.Simulation;
using SmartX.Core.Pulse;

// Messages contain numbers; keep "." as the decimal
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// ---------- Framework services ----------
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddProblemDetails();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

string[] allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                          ?? new[] { "http://localhost:5200" };

builder.Services.AddCors(options =>
    options.AddPolicy("SmartXClient", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials())); // SignalR N pulls connections

// ---------- Options ----------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<PulseOptions>(builder.Configuration.GetSection("Pulse"));
builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection("Simulator"));
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection("Alerts"));

// ---------- Part 1 services ----------
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<TopologyService>();
builder.Services.AddSingleton<TelemetryStore>();
builder.Services.AddSingleton<IncidentBook>();
builder.Services.AddSingleton<PulseMonitor>();
builder.Services.AddSingleton<DeviceRegistrationService>();
builder.Services.AddSingleton<AttachmentStorage>();
builder.Services.AddSingleton<MeterAnalyticsService>();
builder.Services.AddSingleton<SimulatorControl>();

// ---------- Part 2 services ----------
  builder.Services.AddSingleton<AlertCenter>();           // HashSet unique alerts / disconnected nodes
  builder.Services.AddSingleton<CommandService>();        // device shadows + Stack<T> undo / redo
builder.Services.AddSingleton<IngestPipeline>();        // Queue<T> + PriorityQueue lanes
 builder.Services.AddSingleton<ActionEngine>();          // predictive suggestions and insights
builder.Services.AddSingleton<SimWorld>();

builder.Services.AddHostedService<TelemetryDispatcher>();
 builder.Services.AddHostedService<TelemetrySimulator>();

var app = builder.Build();

// ---------- Pipeline ----------
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors("SmartXClient");

app.MapOpenApi(); // http://localhost:5100/openapi/v1.json

app.MapSystemEndpoints();
app.MapDeviceEndpoints();
app.MapTelemetryEndpoints();
app.MapPulseEndpoints();
app.MapTopologyEndpoints();
app.MapAnalyticsEndpoints();
app.MapSimulatorEndpoints();
app.MapStreamEndpoints();
app.MapCommandEndpoints();
app.MapAlertEndpoints();
app.MapEngineEndpoints();
app.MapHub<PulseHub>(PulseHub.Route);

  // Create the engine first so it is subscribed to alerts and commands before anything happens,
// then seed the topology, devices, historical telemetry and a week of operator history.
app.Services.GetRequiredService<ActionEngine>();
    DataSeeder.Seed(app.Services);
EngineSeeder.Seed(app.Services);

app.Run();
