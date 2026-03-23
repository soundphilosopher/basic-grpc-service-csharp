using System.Net.Quic;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();

// Register the gRPC health service and wire it to ASP.NET Core health checks.
// Map("") covers the overall server status, queried by most probes by default.
// Map("...") covers the status of a specific named gRPC service.
builder.Services
    .AddGrpcHealthChecks(o =>
    {
        // "" = overall server status, queried by most probes when no service
        // name is specified. Include all registered checks.
        o.Services.Map("", _ => true);

        // Map the BasicService to only checks tagged "basic".
        o.Services.Map("basic.v1.BasicService", r => r.Tags.Contains("basic"));
    })
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["basic"]);

var app = builder.Build();

// Diagnose QUIC/HTTP3 support early — Kestrel silently skips HTTP/3
// if QUIC is unavailable, with no warning logged at default log levels.
if (app.Logger.IsEnabled(LogLevel.Information))
{
    app.Logger.LogInformation("QUIC (HTTP/3) supported on this system: {Supported}", QuicListener.IsSupported);
}


app.MapGrpcService<BasicService>();

// Health checks are available in every environment —
// Kubernetes, load balancers, and monitoring tools rely on them in production.
app.MapGrpcHealthChecksService();

// Reflection is development-only — it exposes your full schema.
if (app.Environment.IsDevelopment())
{
    app.MapGrpcReflectionService();
}

app.Run();
