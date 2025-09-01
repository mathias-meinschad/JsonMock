namespace JsonMock.Helpers;

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

public static class ResponseHelper {
    public static async Task<IResult> GetResponseByJsonPath(HttpRequest req,
                                                            string jsonPath,
                                                            string basePath) {
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
        string? value = GetByPath(body, jsonPath);

        var filePath = Path.Combine(basePath, $"{value}.json");
        if (!File.Exists(filePath))
            filePath = Path.Combine(basePath, "default.json");

        return GetFileResponse(filePath);
    }

    public static async Task<IResult> GetResponseByJsonPath(HttpRequest req,
                                                            string jsonPath,
                                                            string basePath,
                                                            Func<string?, string> fileResolver) {
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
        var value = GetByPath(body, jsonPath);

        var fileName = fileResolver(value); // returns something like "MERCH.json" or "<value>.json"
        var filePath = Path.Combine(basePath, fileName);

        if (!File.Exists(filePath))
            filePath = Path.Combine(basePath, "default.json");

        return GetFileResponse(filePath);
    }

    public static IResult GetFileResponse(string filePath) {
        if (!File.Exists(filePath))
            return Results.NotFound(new {error = $"File {filePath} not found"});

        var json = File.ReadAllText(filePath);
        var element = JsonSerializer.Deserialize<JsonElement>(json);
        return Results.Json(element);
    }

    private static string? GetByPath(JsonElement element, string path) {
        var parts = path.Split('.');
        JsonElement current = element;

        foreach (var part in parts) {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                return null; // not found
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }


    // Build a resolver from (prefix -> fileName) rules. If no rule matches,
    // it will try "<rawValue>.json" and finally "default.json".
    public static Func<string?, string> StartsWithResolver(IEnumerable<(string Prefix, string File)> rules,
                                                           string fallback = "default.json",
                                                           bool ignoreCase = true,
                                                           bool elseUseRaw = true) {
        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var list = rules.ToArray();

        return v => {
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            foreach (var (prefix, file) in list)
                if (v!.StartsWith(prefix, comp))
                    return EnsureJson(file);
            return elseUseRaw ? EnsureJson(v!) : fallback;
        };
    }

    // Build a resolver from (regex -> fileName) rules (use when you care about length etc.).
    public static Func<string?, string> ResolverFromRegexRules(IEnumerable<(string Pattern, string File)> rules,
                                                               string fallback = "default.json",
                                                               bool elseUseRaw = true) {
        var compiled = rules.Select(r => (Regex: new Regex(r.Pattern, RegexOptions.Compiled), r.File)).ToArray();

        return v => {
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            foreach (var r in compiled)
                if (r.Regex.IsMatch(v!))
                    return EnsureJson(r.File);
            return elseUseRaw ? EnsureJson(v!) : fallback;
        };
    }

    private static string EnsureJson(string name) =>
        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.json";

    public static Func<string?, string> BuildResolverFromConfig(ResolverOptions opts) {
        if (string.Equals(opts.Mode, "regex", StringComparison.OrdinalIgnoreCase)) {
            var rules = opts.Rules.Where(r => !string.IsNullOrWhiteSpace(r.Pattern) && !string.IsNullOrWhiteSpace(r.File)).Select(r => (r.Pattern!, r.File!));
            return ResolverFromRegexRules(rules, opts.Fallback, opts.ElseUseRaw);
        } else {
            var rules = opts.Rules.Where(r => !string.IsNullOrWhiteSpace(r.Prefix) && !string.IsNullOrWhiteSpace(r.File)).Select(r => (r.Prefix!, r.File!));
            return StartsWithResolver(rules, opts.Fallback, opts.IgnoreCase, opts.ElseUseRaw);
        }
    }

}
public class ResolverOptions {
    public string Mode { get; set; } = "startsWith"; // "startsWith" | "regex"
    public string Fallback { get; set; } = "default.json";
    public bool IgnoreCase { get; set; } = true; // startsWith only
    public bool ElseUseRaw { get; set; } = true;
    public List<ResolverRule> Rules { get; set; } = new();
}
public class ResolverRule {
    public string? Prefix { get; set; } // for startsWith
    public string? Pattern { get; set; } // for regex
    public string File { get; set; } = "";
}
