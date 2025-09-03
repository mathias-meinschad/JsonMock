// Program.cs

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JsonMock.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
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

    // ---- Global JSON-aware error handler ----
    app.UseExceptionHandler(errorApp => {
        errorApp.Run(async context => {
            var feature = context.Features.Get<IExceptionHandlerPathFeature>();
            var ex = feature?.Error;

            // If any JSON serialization/deserialization blew up (e.g. malformed mock file)
            if (ex is JsonException) {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                var errorPayload = new {
                    trxId = Guid.NewGuid().ToString(),
                    errorStatus = new {
                        code = "500",
                        text = "Invalid JSON in mock response.",
                        category = "TECHNICAL"
                    }
                };
                await context.Response.WriteAsJsonAsync(errorPayload);
                return;
            }

            // Generic fallback
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            var genericPayload = new {
                trxId = Guid.NewGuid().ToString(),
                errorStatus = new {
                    code = "500",
                    text = "Unexpected server error.",
                    category = "TECHNICAL"
                }
            };
            await context.Response.WriteAsJsonAsync(genericPayload);
        });
    });

    // ---- Request JSON guard for all mock endpoints ----
    app.Use(async (ctx, next) => {
        ctx.Request.EnableBuffering();

        try {
            // Try to parse once up-front; handlers can re-read after we rewind.
            using var _ = await JsonDocument.ParseAsync(ctx.Request.Body);
            ctx.Request.Body.Position = 0;
        } catch (JsonException) {
            ctx.Request.Body.Position = 0;

            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            var badRequestPayload = new {
                trxId = Guid.NewGuid().ToString(),
                errorStatus = new {
                    code = "400",
                    text = "Invalid JSON payload.",
                    category = "TECHNICAL"
                }
            };
            await ctx.Response.WriteAsJsonAsync(badRequestPayload);
            return;
        } catch {
            ctx.Request.Body.Position = 0;

            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            var readErrorPayload = new {
                trxId = Guid.NewGuid().ToString(),
                errorStatus = new {
                    code = "500",
                    text = "Unable to read request body.",
                    category = "TECHNICAL"
                }
            };
            await ctx.Response.WriteAsJsonAsync(readErrorPayload);
            return;
        }

        await next();
    });

    // Small dotted-path getter (local to Program.cs)
    static string? PathGet(JsonElement element, string path) {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        JsonElement cur = element;
        foreach (var part in parts) {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(part, out var next))
                return null;
            cur = next;
        }
        return cur.ValueKind == JsonValueKind.String ? cur.GetString() : cur.ToString();
    }

    // Shared PDG handler implementing the search order:
    // 1) {base}/{method}/{epcId}/{bankCode}/{accountNo}.json
    // 2) {base}/{method}/{epcId}/{bankCode}.json
    // 3) {base}/{method}/{epcId}.json
    // 4) Fallback: old correlationId/resolver/default
    async Task<IResult> SearchForCards(HttpRequest req,
                                       string methodName,
                                       string basePath) {
        var methodDir = Path.Combine(basePath, methodName);

        // Read body but keep it available for the fallback call
        req.EnableBuffering();
        using var doc = await JsonDocument.ParseAsync(req.Body);
        req.Body.Position = 0; // reset for fallback

        var root = doc.RootElement;
        var epcId = PathGet(root, "metaData.systemMetaData.epcIdentification");
        var bankCode = PathGet(root, "cardIdentifier.bankCode");
        var accountNo = PathGet(root, "cardIdentifier.accountNo");

        // 1) {epcId}/{bankCode}/{accountNo}.json
        if (!string.IsNullOrWhiteSpace(epcId) && !string.IsNullOrWhiteSpace(bankCode) && !string.IsNullOrWhiteSpace(accountNo)) {
            var p1 = Path.Combine(methodDir, epcId!, bankCode!, $"{accountNo}.json");
            if (File.Exists(p1))
                return ResponseHelper.GetFileResponse(p1);
        }

        // 2) {epcId}/{bankCode}.json
        if (!string.IsNullOrWhiteSpace(epcId) && !string.IsNullOrWhiteSpace(bankCode)) {
            var p2 = Path.Combine(methodDir, epcId!, $"{bankCode}.json");
            if (File.Exists(p2))
                return ResponseHelper.GetFileResponse(p2);
        }

        // 3) {epcId}.json
        if (!string.IsNullOrWhiteSpace(epcId)) {
            var p3 = Path.Combine(methodDir, $"{epcId}.json");
            if (File.Exists(p3))
                return ResponseHelper.GetFileResponse(p3);
        }

        // 4) Old logic (correlationId + resolver + default)
        return await ResponseHelper.GetResponseByJsonPath(req, "metaData.businessMetaData.correlationId", methodDir, pdgResolver // keep your configured resolver in play
        );
    }

    // ---------------------------
    // SCA endpoints (unchanged)
    // ---------------------------

    #region SCA

    app.MapPost("/scaJsonMock/startAuthentication", async (HttpRequest req) => {
        var response = await SearchForCards(req, "startAuthentication", scaBasePath!);
        // adjust the value of the field scaConsentData.resourceId to a new GUID
        // If the helper returned JSON, tweak scaConsentData.resourceId
        if (response is JsonHttpResult<JsonElement> jsonResult) {
            // Make it mutable
            var root = JsonNode.Parse(jsonResult.Value.GetRawText())!.AsObject();

            if (root["scaConsentData"] is JsonObject sca) {
                // set to a new GUID (create the property if it doesn't exist)
                sca["resourceId"] = Guid.NewGuid().ToString();
            }

            return Results.Json(root, statusCode: jsonResult.StatusCode ?? StatusCodes.Status200OK);
        }

        // Not JSON? Just pass through.
        return response;
    });

    app.MapPost("/scaJsonMock/getAuthenticationStatus", async (HttpRequest req) => {
        var rand = new Random();
        if (rand.Next(0, 10) < 5) {
            // 50% chance
            return ResponseHelper.GetFileResponse(Path.Combine(scaBasePath!, "getAuthenticationStatus", "pending.json"));
        }

        return await SearchForCards(req, "getAuthenticationStatus", scaBasePath!);
    });

    app.MapPost("/scaJsonMock/notifyAuthenticationUpdate", async (HttpRequest req) => await SearchForCards(req, "notifyAuthenticationUpdate", scaBasePath!));

    #endregion


    // ---------------------------
// PDG endpoints (hierarchical lookup + fallback to old logic)
// ---------------------------

    #region PDG

    app.MapPost("/pdgJsonMock/authorizeTokenProvisioning", async (HttpRequest req) => await SearchForCards(req, "authorizeTokenProvisioning", pdgBasePath!));

    app.MapPost("/pdgJsonMock/notifyTokenDigitization", async (HttpRequest req) => await SearchForCards(req, "notifyTokenDigitization", pdgBasePath!));

    app.MapPost("/pdgJsonMock/notifyTokenEvent", async (HttpRequest req) => await SearchForCards(req, "notifyTokenEvent", pdgBasePath!));

    app.MapPost("/pdgJsonMock/activationCodeNotif", async (HttpRequest req) => await SearchForCards(req, "activationCodeNotif", pdgBasePath!));

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
