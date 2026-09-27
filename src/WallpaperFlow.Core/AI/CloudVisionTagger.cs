using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WallpaperFlow.Core.Infrastructure;

namespace WallpaperFlow.Core.AI;

/// <summary>
/// Optional, opt-in cloud tagging through the Anthropic Messages API. Only the 480 px thumbnail is sent,
/// only when the user enabled it and supplied their own API key.
/// </summary>
public sealed class CloudVisionTagger
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private const string Endpoint = "https://api.anthropic.com/v1/messages";

    public sealed record Result(IReadOnlyList<string> Tags, string? Category, string? Description);

    public static async Task<Result?> TagAsync(string apiKey, string model, string imagePath, string title, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
        var media = Path.GetExtension(imagePath).ToLowerInvariant() switch { ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/jpeg" };
        var body = new
        {
            model,
            max_tokens = 300,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image", source = new { type = "base64", media_type = media, data = Convert.ToBase64String(bytes) } },
                        new
                        {
                            type = "text",
                            text = $"This is a frame from a desktop wallpaper titled \"{title}\". Reply with JSON only, no prose: " +
                                   "{\"tags\": [up to 10 short lowercase tags describing subject, style, mood and colours], " +
                                   "\"category\": one of Nature, Space, Anime, Games, City, Cyberpunk, Ocean, Animals, Cars, Fantasy, Abstract, Minimal, Seasonal, Technology, Other, " +
                                   "\"description\": one sentence}",
                        },
                    },
                },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn($"Cloud tagging failed: {(int)resp.StatusCode} {json[..Math.Min(200, json.Length)]}");
            return null;
        }
        return Parse(json);
    }

    public static Result? Parse(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var text = string.Concat(doc.RootElement.GetProperty("content").EnumerateArray()
                .Where(c => c.GetProperty("type").GetString() == "text").Select(c => c.GetProperty("text").GetString()));
            text = text.Replace("```json", "").Replace("```", "").Trim();
            int s = text.IndexOf('{'), e = text.LastIndexOf('}');
            if (s < 0 || e <= s) return null;
            using var inner = JsonDocument.Parse(text[s..(e + 1)]);
            var r = inner.RootElement;
            var tags = r.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length is > 0 and < 40).Take(10).ToList()
                : new List<string>();
            var cat = r.TryGetProperty("category", out var c) ? c.GetString() : null;
            var desc = r.TryGetProperty("description", out var d) ? d.GetString() : null;
            return new Result(tags, cat == "Other" ? null : cat, desc);
        }
        catch { return null; }
    }
}
