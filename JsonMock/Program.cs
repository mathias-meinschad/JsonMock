using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Serilog;

var appBase = AppContext.BaseDirectory;

// ---- Serilog (file) ----
Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().Enrich.FromLogContext().WriteTo
    .File(path: Path.Combine(appBase, "logs", "mock-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true).CreateLogger();

try {
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseWindowsService(options => { options.ServiceName = "World-Direct Json Mock"; });

    builder.WebHost.UseUrls(builder.Configuration["Url"] ?? "localhost:5000");
    // Read SCA base path from config
    var scaBasePath = builder.Configuration["ScaConfig:BasePath"];
    var pdgBasePath = builder.Configuration["PdgConfig:BasePath"];
    var app = builder.Build();

    #region SCA

    app.MapPost("/scaJsonMock/startAuthentication", async (HttpRequest req) => await GetResponseByJsonPath(req, "metaData.businessMetaData.correlationId", $"{scaBasePath}\\startAuthentication"));

    app.MapPost("/scaJsonMock/getAuthenticationStatus", async (HttpRequest req) => {
        var rand = new Random();
        if (rand.Next(0, 10) < 5) {
            // 50% chance 
            return GetFileResponse($@"{scaBasePath}\getAuthenticationStatus\pending.json");
        }

        return await GetResponseByJsonPath(req, "metaData.businessMetaData.correlationId", $"{scaBasePath}\\getAuthenticationStatus");
    });

    app.MapPost("/scaJsonMock/notifyAuthenticationUpdate",
        async (HttpRequest req) => await GetResponseByJsonPath(req, "metaData.businessMetaData.correlationId", $"{scaBasePath}\\notifyAuthenticationUpdate"));

    #endregion


    #region PDG

    app.MapPost("/pdgJsonMock/authorizeTokenProvisioning",
        async (HttpRequest req) => await GetResponseByJsonPath(req, "metaData.businessMetaData.correlationId", $"{pdgBasePath}\\authorizeTokenProvisioning"));

    app.MapPost("/pdgJsonMock/notifyTokenDigitization",
        async (HttpRequest req) => await GetResponseByJsonPath(req, "metaData.businessMetaData.correlationId", $"{pdgBasePath}\\notifyTokenDigitization"));

    app.MapPost("/pdgJsonMock/notifyTokenEvent", async (HttpRequest req) => await GetResponseByJsonPath(req, "metaData.businessMetaData.correlationId", $"{pdgBasePath}\\notifyTokenEvent"));

    app.MapPost("/pdgJsonMock/activationCodeNotif", async (HttpRequest req) => await GetResponseByJsonPath(req, "metaData.businessMetaData.correlationId", $"{pdgBasePath}\\activationCodeNotif"));

    #endregion

    Log.Information("Host starting up.");

    app.Run();

    Log.Information("Host terminated gracefully.");
} catch (Exception ex) {
    Log.Fatal(ex, "Host terminated unexpectedly during startup.");
    throw; // let SCM see the failure, but we still have the logs
} finally {
    Log.CloseAndFlush();
}

async Task<IResult> GetResponseByJsonPath(HttpRequest req,
                                          string path,
                                          string basePath) {
    // Read JSON body
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);

    // Example: pick response file based on "type" field
    string? type = GetByPath(body, path);
    string filePath = $"{basePath}\\{type}.json";

    return GetFileResponse(!File.Exists(filePath) ? $"{basePath}\\default.json" : filePath);
}

IResult GetFileResponse(string filePath) {
    if (!File.Exists(filePath))
        return Results.NotFound(new {error = $"File {filePath} not found"});

    var json = File.ReadAllText(filePath);
    var element = JsonSerializer.Deserialize<JsonElement>(json);

    return Results.Json(element);
}

string? GetByPath(JsonElement element, string path) {
    var parts = path.Split('.');
    JsonElement current = element;

    foreach (var part in parts) {
        if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) {
            return null; // not found
        }
    }

    return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
}
