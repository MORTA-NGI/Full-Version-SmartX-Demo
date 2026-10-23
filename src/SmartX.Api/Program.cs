using SmartX.Api.Endpoints;
using SmartX.Api.Services;
using SmartX.Core.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.Configure<AttachmentOptions>(builder.Configuration.GetSection("Attachments"));
builder.Services.ConfigureHttpJsonOptions(o => SmartXJson.Configure(o.SerializerOptions));
builder.Services.AddProblemDetails();

// All gateway state lives in singletons (in-memory, thread-safe via per-service locks).
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<DeploymentService>();
builder.Services.AddSingleton<TelemetryHub>();
builder.Services.AddSingleton<AnomalyDetector>();
builder.Services.AddSingleton<HistoryService>();
builder.Services.AddSingleton<AlertService>();
builder.Services.AddSingleton<InsightEngine>();
builder.Services.AddSingleton<CommandService>();
builder.Services.AddSingleton<EngagementService>();
builder.Services.AddSingleton<TopologyService>();
builder.Services.AddSingleton<AttachmentStore>();
builder.Services.AddSingleton<RegistrationService>();
builder.Services.AddSingleton<IngestionGateway>();
builder.Services.AddSingleton<DataSeeder>();

// The stream processor is both a singleton (endpoints enqueue into it) and a hosted background worker.
builder.Services.AddSingleton<StreamProcessor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamProcessor>());
builder.Services.AddHostedService<DeviceWatchdog>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Auto-open an issue on the Anomaly Radar whenever a critical alert fires.
var engagement = app.Services.GetRequiredService<EngagementService>();
app.Services.GetRequiredService<AlertService>().CriticalRaised += engagement.OnCriticalAlert;

app.Services.GetRequiredService<DataSeeder>().Seed();

app.MapGet("/", () => Results.Redirect("/health"));
app.MapGet("/health", (DeviceRegistry registry) => Results.Ok(new { status = "healthy", service = "Smart-X Gateway API", devices = registry.Count, utc = DateTime.UtcNow }));

app.MapIngestionEndpoints();
app.MapStreamEndpoints();
app.MapTopologyEndpoints();

app.Run();

/// <summary>Exposed for integration tests.</summary>
public partial class Program;
