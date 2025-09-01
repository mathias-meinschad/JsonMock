// Program.cs

using System.Text.Json;
using System.Text.RegularExpressions;
using JsonMock.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

var appBase = AppContext.BaseDirectory;

// ---- Serilog (file) ----
Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().Enrich.FromLogContext().WriteTo
    .File(Path.Combine(appBase, "logs", "mock-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true).CreateLogger();

try {
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseWindowsService(options => { options.ServiceName = "World-Direct Json Mock"; });

    builder.WebHost.UseUrls(builder.Configuration["Url"] ?? "localhost:5000");
    // Read SCA base path from config
    var scaBasePath = builder.Configuration["ScaConfig:BasePath"];
    var pdgBasePath = builder.Configuration["PdgConfig:BasePath"];
    
    // Bind resolver options from config
    var pdgResolverOptions = new ResolverOptions();
    builder.Configuration.GetSection("PdgConfig:Resolver").Bind(pdgResolverOptions);
    // Build the resolver function
    var pdgResolver = ResponseHelper.BuildResolverFromConfig(pdgResolverOptions);
    
    var app = builder.Build();

    // ---------------------------
    // SCA endpoints (unchanged)
    // ---------------------------

    #region SCA

    app.MapPost("/scaJsonMock/startAuthentication",
        async (HttpRequest req) => await ResponseHelper.GetResponseByJsonPath(req, jsonPath: "metaData.businessMetaData.correlationId", basePath: Path.Combine(scaBasePath!, "startAuthentication")));

    app.MapPost("/scaJsonMock/getAuthenticationStatus", async (HttpRequest req) => {
        var rand = new Random();
        if (rand.Next(0, 10) < 5) {
            // 50% chance
            return ResponseHelper.GetFileResponse(Path.Combine(scaBasePath!, "getAuthenticationStatus", "pending.json"));
        }

        return await ResponseHelper.GetResponseByJsonPath(req, jsonPath: "metaData.businessMetaData.correlationId", basePath: Path.Combine(scaBasePath!, "getAuthenticationStatus"));
    });

    app.MapPost("/scaJsonMock/notifyAuthenticationUpdate",
        async (HttpRequest req) => await ResponseHelper.GetResponseByJsonPath(req, jsonPath: "metaData.businessMetaData.correlationId", basePath: Path.Combine(scaBasePath!, "notifyAuthenticationUpdate")));

    #endregion


    // ---------------------------
    // PDG endpoints (using resolver)
    // ---------------------------

    #region PDG

    app.MapPost("/pdgJsonMock/authorizeTokenProvisioning",
        (HttpRequest req) => ResponseHelper.GetResponseByJsonPath(req, "metaData.businessMetaData.correlationId", Path.Combine(pdgBasePath!, "authorizeTokenProvisioning"),
            pdgResolver));

    app.MapPost("/pdgJsonMock/notifyTokenDigitization",
        (HttpRequest req) => ResponseHelper.GetResponseByJsonPath(req, "metaData.businessMetaData.correlationId", Path.Combine(pdgBasePath!, "notifyTokenDigitization"),
            pdgResolver));

    app.MapPost("/pdgJsonMock/notifyTokenEvent",
        (HttpRequest req) => ResponseHelper.GetResponseByJsonPath(req, "metaData.businessMetaData.correlationId", Path.Combine(pdgBasePath!, "notifyTokenEvent"), pdgResolver));

    app.MapPost("/pdgJsonMock/activationCodeNotif",
        (HttpRequest req) => ResponseHelper.GetResponseByJsonPath(req, "metaData.businessMetaData.correlationId", Path.Combine(pdgBasePath!, "activationCodeNotif"), pdgResolver));

    #endregion

    Log.Information("Host starting up.");
    app.Run();
    Log.Information("Host terminated gracefully.");
} catch (Exception ex) {
    Log.Fatal(ex, "Host terminated unexpectedly during startup.");
    throw;
} finally {
    Log.CloseAndFlush();
}