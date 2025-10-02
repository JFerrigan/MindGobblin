using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities; // for QueryHelpers
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// ---- Config (Spotify via env; keep secrets out of code) ----
string? SPOTIFY_CLIENT_ID     = Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_ID");
string? SPOTIFY_CLIENT_SECRET = Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_SECRET");
string  SPOTIFY_REDIRECT_URI  = Environment.GetEnvironmentVariable("SPOTIFY_REDIRECT_URI") ?? "http://127.0.0.1:5173/callback";
string  SPOTIFY_SCOPES        = "playlist-modify-public playlist-modify-private";

// ---- Clash Royale config ----
string? CR_TOKEN = Environment.GetEnvironmentVariable("CR_TOKEN"); // <-- set this in env

// ---- HttpClient for Clash Royale ----
builder.Services.AddHttpClient("clashroyale", c =>
{
    c.BaseAddress = new Uri("https://api.clashroyale.com"); // no trailing slash
});

// ---- Kestrel binding (loopback; supports localhost + 127.0.0.1) ----
var inContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

builder.WebHost.ConfigureKestrel(k =>
{
    if (!inContainer){
         k.ListenLocalhost(8080);
    }
});

// ---- CORS ----
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// ---- JSON ----
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// ---- Sessions ----
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(o =>
{
    o.Cookie.Name = "mg_sess";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.None; // HTTP ok in dev
    o.Cookie.IsEssential = true;
    o.IdleTimeout = TimeSpan.FromHours(8);
});

// ---- HttpClient(s) for Spotify ----
builder.Services.AddHttpClient("spotify").ConfigureHttpClient(c =>
{
    c.BaseAddress = new Uri("https://api.spotify.com"); // NOTE: no /v1 here
});
builder.Services.AddHttpClient("spotify-accounts"); // token endpoint

// ---- In-memory storage (demo) ----
var scores = new List<Score>();

// ---- Local fallback for (deprecated) genre seeds ----
var LOCAL_GENRE_SEEDS = new[]
{
    "acoustic","afrobeat","alt-rock","alternative","ambient","anime","black-metal","bluegrass","blues",
    "bossanova","brazil","breakbeat","british","cantopop","chicago-house","children","chill","classical",
    "club","comedy","country","dance","dancehall","death-metal","deep-house","detroit-techno","disco",
    "disney","drum-and-bass","dub","dubstep","edm","electro","electronic","emo","folk","forro","french",
    "funk","garage","german","gospel","goth","groove","grunge","guitar","happy","hard-rock","hardcore",
    "hardstyle","heavy-metal","hip-hop","holidays","honky-tonk","house","idm","indian","indie","indie-pop",
    "industrial","iranian","j-dance","j-idol","j-pop","j-rock","jazz","k-pop","kids","latin","latino",
    "malay","mandopop","metal","metalcore","minimal-techno","movies","mpb","new-age","new-release","opera",
    "pagode","party","philippines-opm","piano","pop","pop-film","post-dubstep","power-pop","progressive-house",
    "psych-rock","punk","punk-rock","r-n-b","rainy-day","reggae","reggaeton","road-trip","rock",
    "rock-n-roll","rockabilly","romance","sad","salsa","samba","sertanejo","show-tunes","singer-songwriter",
    "ska","sleep","soul","soundtracks","spanish","study","summer","swedish","synth-pop","tango","techno",
    "trance","trip-hop","turkish","work-out","world-music"
};

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions {
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSession();

// tiny request logger
app.Use(async (ctx, next) =>
{
    await next();
    var ep = ctx.GetEndpoint();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {ctx.Request.Host} {ctx.Request.Path} -> {(ep?.DisplayName ?? "NO MATCH")} ({ctx.Response.StatusCode})");
});

// ---------- Minimal endpoints ----------
app.MapGet("/health", () => new { ok = true, serverTime = DateTimeOffset.UtcNow });

app.MapPost("/echo", (EchoRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Message))
        return Results.BadRequest(new { error = "message is required" });

    return Results.Ok(new { youSaid = req.Message, len = req.Message.Length });
});

app.MapPost("/score", (ScoreRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Username))
        return Results.BadRequest(new { error = "username is required" });
    if (req.Value is null)
        return Results.BadRequest(new { error = "value is required" });

    var s = new Score
    {
        Username = req.Username.Trim(),
        Value = req.Value.Value,
        At = DateTimeOffset.UtcNow
    };
    scores.Add(s);

    return Results.Created($"/score/{scores.Count - 1}", s);
});

app.MapGet("/score/top", (int? limit) =>
{
    var n = Math.Clamp(limit ?? 10, 1, 100);
    var top = scores
        .OrderByDescending(s => s.Value)
        .ThenBy(s => s.At)
        .Take(n)
        .ToList();
    return Results.Ok(top);
});

// list all mapped endpoints (debug)
app.MapGet("/debug/routes", (EndpointDataSource ds) =>
{
    var list = ds.Endpoints
        .OfType<RouteEndpoint>()
        .Select(e => new { route = e.RoutePattern.RawText, methods = string.Join(",", e.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods ?? new[] { "ANY" }) });
    return Results.Ok(list);
});

// ----------Royale Helpers-------------
// ---------- Clash Royale proxy (GET pass-through) ----------
app.MapGet("/cr/{**path}", async (HttpContext ctx, string path, IHttpClientFactory http) =>
{
    // Require CR_TOKEN in env
    string Require(string? v, string name) =>
        !string.IsNullOrWhiteSpace(v) ? v : throw new Exception($"Missing environment variable {name}");

    var crToken = Require(Environment.GetEnvironmentVariable("CR_TOKEN"), "CR_TOKEN");

    // Build upstream URL: https://api.clashroyale.com/<path>?<query>
    var qs = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : "";
    var upstreamPath = "/" + (path?.TrimStart('/') ?? "");
    var client = http.CreateClient("clashroyale");

    var req = new HttpRequestMessage(HttpMethod.Get, upstreamPath + qs);
    req.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", crToken);

    if (ctx.Request.Headers.TryGetValue("User-Agent", out var ua))
        req.Headers.TryAddWithoutValidation("User-Agent", ua.ToString());

    using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    var contentType = res.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";
    var text = await res.Content.ReadAsStringAsync(ctx.RequestAborted);

    // Return as text content with the SAME status code as upstream
    return Results.Content(text, contentType, System.Text.Encoding.UTF8, (int)res.StatusCode);
})
.WithDisplayName("Clash Royale Proxy (GET)");

// ---------- Spotify helpers ----------
string Require(string? v, string name) =>
    !string.IsNullOrWhiteSpace(v) ? v : throw new Exception($"Missing environment variable {name}");

string RandomHex(int bytes = 16)
{
    var b = RandomNumberGenerator.GetBytes(bytes);
    return Convert.ToHexString(b);
}

async Task EnsureAccessToken(HttpContext ctx)
{
    var expiresAt = ctx.Session.GetString("expires_at");
    if (!string.IsNullOrEmpty(expiresAt) && DateTimeOffset.TryParse(expiresAt, out var exp) && exp > DateTimeOffset.UtcNow.AddSeconds(15))
        return;

    var refresh = ctx.Session.GetString("refresh_token") ?? throw new Exception("Not logged in");
    var body = new Dictionary<string,string> {
        ["grant_type"]    = "refresh_token",
        ["refresh_token"] = refresh,
        ["client_id"]     = Require(SPOTIFY_CLIENT_ID, "SPOTIFY_CLIENT_ID"),
        ["client_secret"] = Require(SPOTIFY_CLIENT_SECRET, "SPOTIFY_CLIENT_SECRET")
    };

    var acct = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("spotify-accounts");
    var res = await acct.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(body));
    if (!res.IsSuccessStatusCode) throw new Exception("Refresh failed");
    var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    var access = json.GetProperty("access_token").GetString()!;
    var expIn  = json.GetProperty("expires_in").GetInt32();

    ctx.Session.SetString("access_token", access);
    ctx.Session.SetString("expires_at", DateTimeOffset.UtcNow.AddSeconds(expIn).ToString("O"));

    if (json.TryGetProperty("refresh_token", out var rt) && rt.GetString() is string newRt && !string.IsNullOrEmpty(newRt))
        ctx.Session.SetString("refresh_token", newRt);
}

async Task<HttpClient> GetSpotifyApi(HttpContext ctx)
{
    await EnsureAccessToken(ctx);
    var token = ctx.Session.GetString("access_token") ?? throw new Exception("Not logged in");
    var api = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("spotify");
    api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return api;
}

// ---------- OAuth flow ----------
app.MapGet("/login", (HttpContext ctx) =>
{
    var state = RandomHex();
    ctx.Session.SetString("oauth_state", state);

    // remember where to return (exact scheme+host the user used)
    var returnTo = $"{ctx.Request.Scheme}://{ctx.Request.Host}/spotify.html";
    ctx.Session.SetString("return_to", returnTo);

    var qs = new QueryString()
        .Add("response_type", "code")
        .Add("client_id", Require(SPOTIFY_CLIENT_ID, "SPOTIFY_CLIENT_ID"))
        .Add("scope", SPOTIFY_SCOPES)
        .Add("redirect_uri", SPOTIFY_REDIRECT_URI)
        .Add("state", state);

    return Results.Redirect("https://accounts.spotify.com/authorize" + qs.ToUriComponent());
});

app.MapGet("/callback", async (HttpContext ctx) =>
{
    var code  = ctx.Request.Query["code"].ToString();
    var state = ctx.Request.Query["state"].ToString();
    var err   = ctx.Request.Query["error"].ToString();

    if (!string.IsNullOrEmpty(err)) return Results.BadRequest($"Auth error: {err}");

    var expected = ctx.Session.GetString("oauth_state");
    ctx.Session.Remove("oauth_state");
    if (string.IsNullOrEmpty(expected) || expected != state) return Results.BadRequest("Invalid state");

    var body = new Dictionary<string,string> {
        ["grant_type"]   = "authorization_code",
        ["code"]         = code,
        ["redirect_uri"] = SPOTIFY_REDIRECT_URI,
        ["client_id"]    = Require(SPOTIFY_CLIENT_ID, "SPOTIFY_CLIENT_ID"),
        ["client_secret"]= Require(SPOTIFY_CLIENT_SECRET, "SPOTIFY_CLIENT_SECRET")
    };

    var acct = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("spotify-accounts");
    var res = await acct.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(body));
    if (!res.IsSuccessStatusCode) return Results.Problem("Token exchange failed");
    var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    var access  = json.GetProperty("access_token").GetString()!;
    var refresh = json.GetProperty("refresh_token").GetString()!;
    var expIn   = json.GetProperty("expires_in").GetInt32();

    ctx.Session.SetString("access_token", access);
    ctx.Session.SetString("refresh_token", refresh);
    ctx.Session.SetString("expires_at", DateTimeOffset.UtcNow.AddSeconds(expIn).ToString("O"));

    // cache /me (optional)
    var api = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("spotify");
    api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
    var meRes = await api.GetAsync("/v1/me");
    if (meRes.IsSuccessStatusCode)
    {
        var me = await meRes.Content.ReadAsStringAsync();
        ctx.Session.SetString("me_cache", me);
    }

    var returnTo = ctx.Session.GetString("return_to");
    ctx.Session.Remove("return_to");
    return Results.Redirect(!string.IsNullOrWhiteSpace(returnTo) ? returnTo! : "/spotify.html");
});

// ---------- API: who am I ----------
app.MapGet("/api/me", async (HttpContext ctx) =>
{
    try { await EnsureAccessToken(ctx); }
    catch { return Results.Json(new { loggedIn = false }); }

    var cached = ctx.Session.GetString("me_cache");
    if (!string.IsNullOrEmpty(cached))
        return Results.Json(new { loggedIn = true, me = JsonSerializer.Deserialize<object>(cached) });

    var api = await GetSpotifyApi(ctx);
    var meRes = await api.GetAsync("/v1/me");
    var text = await meRes.Content.ReadAsStringAsync();
    if (!meRes.IsSuccessStatusCode) return Results.Json(new { loggedIn = false });

    ctx.Session.SetString("me_cache", text);
    return Results.Json(new { loggedIn = true, me = JsonSerializer.Deserialize<object>(text) });
});

// ---------- API: available genres (with fallback) ----------
app.MapGet("/api/genres", async (HttpContext ctx) =>
{
    try
    {
        var api = await GetSpotifyApi(ctx);
        Console.WriteLine("[Spotify] GET /v1/recommendations/available-genre-seeds");
        var r = await api.GetAsync("/v1/recommendations/available-genre-seeds");
        var body = await r.Content.ReadAsStringAsync();

        if (r.IsSuccessStatusCode)
        {
            try
            {
                var doc = JsonDocument.Parse(body).RootElement;
                if (doc.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
                {
                    var list = g.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s));
                    return Results.Json(new { genres = list });
                }
            }
            catch { /* fall through */ }
            return Results.Content(body, "application/json", Encoding.UTF8);
        }

        // If Spotify returns 404 or anything non-2xx, fall back to local snapshot
        Console.WriteLine($"[Spotify] /available-genre-seeds status {r.StatusCode} — using local fallback list");
        return Results.Json(new { genres = LOCAL_GENRE_SEEDS });
    }
    catch
    {
        // Not logged in / token issue → still give local list so UI can work
        return Results.Json(new { genres = LOCAL_GENRE_SEEDS, from = "local" });
    }
});

// ---------- API: create random-by-genre playlist ----------
// Tries Spotify Recommendations first; if 404, falls back to:
//  - search artists by genre, sample some artists, pull their top-tracks, and build a list.
app.MapPost("/api/create-random-playlist", async (HttpContext ctx) =>
{
    try
    {
        var api = await GetSpotifyApi(ctx);

        // Who am I?
        var meRes = await api.GetAsync("/v1/me");
        if (!meRes.IsSuccessStatusCode)
            return Results.Content(await meRes.Content.ReadAsStringAsync(), "application/json", Encoding.UTF8, (int)meRes.StatusCode);
        var meDoc = JsonDocument.Parse(await meRes.Content.ReadAsStringAsync()).RootElement;
        var userId = meDoc.GetProperty("id").GetString()!;

        // Parse body { genre, count, name?, description?, isPublic? }
        using var bodyDoc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var reqRoot = bodyDoc.RootElement;

        string genre = reqRoot.TryGetProperty("genre", out var g) ? (g.GetString() ?? "").Trim().ToLowerInvariant() : "";
        int count = reqRoot.TryGetProperty("count", out var c) ? Math.Clamp(c.GetInt32(), 1, 100) : 25;
        string name = reqRoot.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()) ? n.GetString()! : $"🎲 {genre} mix — {DateTime.Now:yyyy-MM-dd HH:mm}";
        string description = reqRoot.TryGetProperty("description", out var d) ? (d.GetString() ?? $"Random {genre} picks via MindGobblin") : $"Random {genre} picks via MindGobblin";
        bool isPublic = reqRoot.TryGetProperty("isPublic", out var isPubEl) && isPubEl.GetBoolean();

        if (string.IsNullOrWhiteSpace(genre))
            return Results.BadRequest(new { error = "genre is required" });

        // Try Recommendations first (may 404 now)
        var recsUrl = "https://api.spotify.com/v1/recommendations";
        var recsParams = new Dictionary<string, string?>
        {
            ["seed_genres"] = genre,
            ["limit"] = Math.Clamp(count, 1, 100).ToString(),
            ["market"] = "from_token"
        };
        var recsFull = QueryHelpers.AddQueryString(recsUrl, recsParams!);
        Console.WriteLine($"[Spotify] GET {recsFull}");
        var recsRes = await api.GetAsync(recsFull);
        var tracks = new List<string>();

        if (recsRes.IsSuccessStatusCode)
        {
            var recsText = await recsRes.Content.ReadAsStringAsync();
            var recDoc = JsonDocument.Parse(recsText).RootElement;
            tracks = recDoc.GetProperty("tracks")
                           .EnumerateArray()
                           .Select(t => t.GetProperty("uri").GetString()!)
                           .Where(u => !string.IsNullOrWhiteSpace(u))
                           .Take(count).ToList();
        }
        else
        {
            // --- Fallback path(s) ---

            // 0) Normalize some common genre synonyms
            string NormalizeGenre(string gStr) => gStr switch
            {
                "hip hop" or "hiphop" => "hip-hop",
                "rnb" or "r&b" or "r and b" => "r-n-b",
                "drum and bass" => "drum-and-bass",
                "alt rock" => "alt-rock",
                _ => gStr
            };
            genre = NormalizeGenre(genre);

            // Get user's country from /me (we already fetched meDoc above)
            string userMarket = meDoc.TryGetProperty("country", out var cc) && cc.ValueKind == JsonValueKind.String
                ? cc.GetString() ?? "US"
                : "US";

            var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddUris(IEnumerable<string?> source)
            {
                foreach (var u in source)
                {
                    if (string.IsNullOrWhiteSpace(u)) continue;
                    if (seenUris.Add(u))
                    {
                        tracks.Add(u);
                        if (tracks.Count >= count) break;
                    }
                }
            }

            // 1) Try ARTIST search by genre + top-tracks
            Console.WriteLine($"[Spotify] recommendations status {recsRes.StatusCode} — falling back to artist search");
            var searchParams = new Dictionary<string, string?>
            {
                ["q"] = $"genre:\"{genre}\"",
                ["type"] = "artist",
                ["market"] = userMarket,
                ["limit"] = "50"
            };
            var searchUrl = QueryHelpers.AddQueryString("https://api.spotify.com/v1/search", searchParams);
            Console.WriteLine($"[Spotify] GET {searchUrl}");
            var searchRes = await api.GetAsync(searchUrl);

            if (searchRes.IsSuccessStatusCode)
            {
                var searchText = await searchRes.Content.ReadAsStringAsync();
                JsonElement artistsItems;

                try
                {
                    var artistsRoot = JsonDocument.Parse(searchText).RootElement;
                    if (artistsRoot.TryGetProperty("artists", out var artistsObj) && artistsObj.ValueKind == JsonValueKind.Object &&
                        artistsObj.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                    {
                        artistsItems = itemsEl;
                    }
                    else
                    {
                        Console.WriteLine("[Spotify] Unexpected artist search shape; payload:");
                        Console.WriteLine(searchText);
                        artistsItems = default;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Spotify] Failed to parse artist search JSON: " + ex.Message);
                    artistsItems = default;
                }

                if (artistsItems.ValueKind == JsonValueKind.Array)
                {
                    var artistIds = new List<string>();
                    foreach (var aEl in artistsItems.EnumerateArray())
                    {
                        if (aEl.ValueKind != JsonValueKind.Object) continue;
                        if (aEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                        {
                            var id = idEl.GetString();
                            if (!string.IsNullOrEmpty(id)) artistIds.Add(id);
                        }
                    }

                    if (artistIds.Count > 0)
                    {
                        // Shuffle artists; try 5–15 of them
                        artistIds = artistIds
                            .OrderBy(_ => BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)))
                            .ToList();

                        var artistSample = artistIds.Take(Math.Max(5, Math.Min(150, count / 2))).ToList();

                        foreach (var artistId in artistSample)
                        {
                            var topParams = new Dictionary<string, string?> { ["market"] = userMarket };
                            var topUrl = QueryHelpers.AddQueryString($"https://api.spotify.com/v1/artists/{artistId}/top-tracks", topParams);
                            Console.WriteLine($"[Spotify] GET {topUrl}");
                            var topRes = await api.GetAsync(topUrl);
                            if (!topRes.IsSuccessStatusCode) continue;

                            var topText = await topRes.Content.ReadAsStringAsync();
                            try
                            {
                                var topTracksRoot = JsonDocument.Parse(topText).RootElement;
                                if (topTracksRoot.TryGetProperty("tracks", out var tArr) && tArr.ValueKind == JsonValueKind.Array)
                                {
                                    var uris = tArr.EnumerateArray()
                                        .Where(tEl => tEl.ValueKind == JsonValueKind.Object && tEl.TryGetProperty("uri", out _))
                                        .Select(tEl => tEl.GetProperty("uri").GetString());
                                    AddUris(uris);
                                }
                                else
                                {
                                    Console.WriteLine("[Spotify] Unexpected top-tracks shape; payload:");
                                    Console.WriteLine(topText);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("[Spotify] Failed to parse top-tracks JSON: " + ex.Message);
                            }

                            if (tracks.Count >= count) break;
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine($"[Spotify] Artist search failed: {(int)searchRes.StatusCode} {searchRes.StatusCode}");
            }

            // 2) If still short, mine PLAYLISTS with the genre in the title/desc
            if (tracks.Count < count)
            {
                Console.WriteLine("[Spotify] artist fallback insufficient — trying playlist search");
                var plParams = new Dictionary<string, string?>
                {
                    ["q"] = genre,
                    ["type"] = "playlist",
                    ["market"] = userMarket,
                    ["limit"] = "10"
                };
                var plUrl = QueryHelpers.AddQueryString("https://api.spotify.com/v1/search", plParams);
                Console.WriteLine($"[Spotify] GET {plUrl}");
                var plRes = await api.GetAsync(plUrl);

                if (plRes.IsSuccessStatusCode)
                {
                    var plText = await plRes.Content.ReadAsStringAsync();
                    List<string> playlistIds = new();
                    try
                    {
                        var playlistsRoot = JsonDocument.Parse(plText).RootElement;
                        if (playlistsRoot.TryGetProperty("playlists", out var plsObj) && plsObj.ValueKind == JsonValueKind.Object &&
                            plsObj.TryGetProperty("items", out var items2El) && items2El.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var playlistItem in items2El.EnumerateArray())
                            {
                                if (playlistItem.ValueKind != JsonValueKind.Object) continue;
                                if (playlistItem.TryGetProperty("id", out var idEl2) && idEl2.ValueKind == JsonValueKind.String)
                                {
                                    var id = idEl2.GetString();
                                    if (!string.IsNullOrEmpty(id)) playlistIds.Add(id);
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("[Spotify] Unexpected playlist search shape; payload:");
                            Console.WriteLine(plText);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[Spotify] Failed to parse playlist search JSON: " + ex.Message);
                    }

                    foreach (var pid in playlistIds.Take(5))
                    {
                        var trkUrl = "https://api.spotify.com/v1/playlists/" + pid + "/tracks?limit=100&fields=items(track(uri))";
                        Console.WriteLine($"[Spotify] GET {trkUrl}");
                        var trkRes = await api.GetAsync(trkUrl);
                        if (!trkRes.IsSuccessStatusCode) continue;

                        var tText = await trkRes.Content.ReadAsStringAsync();
                        try
                        {
                            var playlistTracksRoot = JsonDocument.Parse(tText).RootElement;
                            if (playlistTracksRoot.TryGetProperty("items", out var items3El) && items3El.ValueKind == JsonValueKind.Array)
                            {
                                var uris = items3El.EnumerateArray()
                                    .Where(itemEl => itemEl.ValueKind == JsonValueKind.Object &&
                                                     itemEl.TryGetProperty("track", out var trEl) &&
                                                     trEl.ValueKind == JsonValueKind.Object &&
                                                     trEl.TryGetProperty("uri", out _))
                                    .Select(itemEl => itemEl.GetProperty("track").GetProperty("uri").GetString());

                                AddUris(uris);
                            }
                            else
                            {
                                Console.WriteLine("[Spotify] Unexpected playlist tracks shape; payload:");
                                Console.WriteLine(tText);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[Spotify] Failed to parse playlist tracks JSON: " + ex.Message);
                        }

                        if (tracks.Count >= count) break;
                    }
                }
                else
                {
                    Console.WriteLine($"[Spotify] Playlist search failed: {(int)plRes.StatusCode} {plRes.StatusCode}");
                }
            }
        }

        if (tracks.Count == 0)
            return Results.BadRequest(new { error = "No tracks found for that genre." });

        // Create playlist
        var createPayload = JsonContent.Create(new { name, description, @public = isPublic });
        var createRes = await api.PostAsync($"/v1/users/{Uri.EscapeDataString(userId)}/playlists", createPayload);
        var createText = await createRes.Content.ReadAsStringAsync();
        if (!createRes.IsSuccessStatusCode)
            return Results.Content(createText, "application/json", Encoding.UTF8, (int)createRes.StatusCode);
        var playlistId = JsonDocument.Parse(createText).RootElement.GetProperty("id").GetString()!;

        // Add tracks
        var addPayload = JsonContent.Create(new { uris = tracks.Take(100).ToArray() });
        var addRes = await api.PostAsync($"/v1/playlists/{playlistId}/tracks", addPayload);
        if (!addRes.IsSuccessStatusCode)
        {
            var addTxt = await addRes.Content.ReadAsStringAsync();
            return Results.Ok(new { ok = true, playlist = JsonSerializer.Deserialize<object>(createText), addError = addTxt });
        }

        return Results.Ok(new { ok = true, playlist = JsonSerializer.Deserialize<object>(createText) });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// ---------- API: create playlist (manual) ----------
app.MapPost("/api/create-playlist", async (HttpContext ctx) =>
{
    try { await EnsureAccessToken(ctx); }
    catch { return Results.Unauthorized(); }

    var api = await GetSpotifyApi(ctx);

    // user id
    var meRes = await api.GetAsync("/v1/me");
    if (!meRes.IsSuccessStatusCode) return Results.StatusCode((int)meRes.StatusCode);
    var meJson = JsonDocument.Parse(await meRes.Content.ReadAsStringAsync()).RootElement;
    var userId = meJson.GetProperty("id").GetString()!;

    // parse body
    using var bodyDoc = await JsonDocument.ParseAsync(ctx.Request.Body);
    string name = bodyDoc.RootElement.TryGetProperty("name", out var n) && n.GetString() is string ns && ns.Length > 0 ? ns : $"My Playlist {DateTime.Now}";
    string description = bodyDoc.RootElement.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "Created by MindGobblin";
    bool isPublic = bodyDoc.RootElement.TryGetProperty("isPublic", out var isPubEl) && isPubEl.GetBoolean();

    var uris = new List<string>();
    if (bodyDoc.RootElement.TryGetProperty("uris", out var u) && u.ValueKind == JsonValueKind.Array)
    {
        foreach (var it in u.EnumerateArray())
        {
            var s = it.GetString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            s = s.Trim();
            if (!s.StartsWith("spotify:track:"))
            {
                var idx = s.IndexOf("/track/", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var id = s[(idx + 7)..].Split('?', '#', '/')[0];
                    s = $"spotify:track:{id}";
                }
            }
            uris.Add(s);
        }
    }

    // 1) create playlist
    var createPayload = JsonContent.Create(new { name, description, @public = isPublic });
    var createRes = await api.PostAsync($"/v1/users/{Uri.EscapeDataString(userId)}/playlists", createPayload);
    var createText = await createRes.Content.ReadAsStringAsync();
    if (!createRes.IsSuccessStatusCode)
        return Results.Content(createText, "application/json", Encoding.UTF8, (int)createRes.StatusCode);

    var playlistObj = JsonSerializer.Deserialize<object>(createText)!;
    var playlistId = JsonDocument.Parse(createText).RootElement.GetProperty("id").GetString()!;

    // 2) add tracks (optional, <=100)
    if (uris.Count > 0)
    {
        var addPayload = JsonContent.Create(new { uris = uris.Take(100).ToArray() });
        var addRes = await api.PostAsync($"/v1/playlists/{playlistId}/tracks", addPayload);
        if (!addRes.IsSuccessStatusCode)
        {
            var addTxt = await addRes.Content.ReadAsStringAsync();
            return Results.Ok(new { ok = true, playlist = playlistObj, addError = addTxt });
        }
    }

    return Results.Ok(new { ok = true, playlist = playlistObj });
});

// ---------- Logout ----------
app.MapPost("/logout", (HttpContext ctx) =>
{
    ctx.Session.Clear();
    return Results.Redirect("/spotify.html");
});

//PLACE STUFF
// ---------- r/place endpoints ----------

// Meta
app.MapGet("/api/place/meta", () =>
{
    return Results.Json(new {
        width = PlaceBoard.Width,
        height = PlaceBoard.Height,
        palette = PlaceBoard.Palette,
        cooldownSeconds = 1,
        since = PlaceBoard.LastTs
    });
});

// Whole board (base64-encoded raw bytes of color indices)
app.MapGet("/api/place/board", () =>
{
    lock (PlaceBoard.Gate)
    {
        var base64 = Convert.ToBase64String(PlaceBoard.Pixels);
        return Results.Text(base64, "text/plain", Encoding.UTF8);
    }
});

// Incremental updates since a timestamp
app.MapGet("/api/place/updates", (long? since) =>
{
    var s = since ?? 0;
    lock (PlaceBoard.Gate)
    {
        var ups = PlaceBoard.Recent.Where(u => u.Ts > s).ToArray();
        return Results.Json(new { since = PlaceBoard.LastTs, updates = ups });
    }
});

// Place a single pixel
app.MapPost("/api/place/set", async (HttpContext ctx) =>
{
    // parse body
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    var root = doc.RootElement;

    if (!root.TryGetProperty("x", out var xEl) || !root.TryGetProperty("y", out var yEl) || !root.TryGetProperty("colorIndex", out var cEl))
        return Results.BadRequest(new { error = "x, y, colorIndex required" });

    int x = xEl.GetInt32();
    int y = yEl.GetInt32();
    int ci = cEl.GetInt32();

    if (x < 0 || y < 0 || x >= PlaceBoard.Width || y >= PlaceBoard.Height)
        return Results.BadRequest(new { error = "out of bounds" });

    if (ci < 0 || ci >= PlaceBoard.Palette.Length)
        return Results.BadRequest(new { error = "invalid colorIndex" });

    // cooldown
    const int Cooldown = 1;
    if (!CheckCooldown(ctx, Cooldown, out var remain))
    {
        return Results.Json(
            new { error = "cooldown", seconds = remain },
            statusCode: 429
        );
    }

    // place
    lock (PlaceBoard.Gate)
    {
        PlaceBoard.Pixels[y * PlaceBoard.Width + x] = (byte)ci;
        PlaceBoard.AddUpdate(x, y, (byte)ci);
        PlaceBoard.Save();
    }

    return Results.Ok(new { ok = true, since = PlaceBoard.LastTs });
});



PlaceBoard.Load();

app.Run();

static bool CheckCooldown(HttpContext ctx, int cooldownSeconds, out int remainSeconds)
{
    var key = "place_last_ts";
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var lastStr = ctx.Session.GetString(key);
    if (long.TryParse(lastStr, out var last))
    {
        var remain = (last + cooldownSeconds) - now;
        if (remain > 0) { remainSeconds = (int)remain; return false; }
    }
    ctx.Session.SetString(key, now.ToString());
    remainSeconds = 0;
    return true;
}


// ---------- r/place board (globals) ----------
// ---------- r/place board (globals) ----------
record PlaceUpdate(int X, int Y, byte ColorIndex, long Ts);

static class PlaceBoard
{
    public const int Width = 256;
    public const int Height = 256;

    // Prefer env; fall back to /data; last resort inside container
    public static readonly string DataPath =
        Environment.GetEnvironmentVariable("PLACE_DATA_PATH")
        ?? "/data/place-board.bin";

    public static readonly string[] Palette = new[]
    {
        "#FF4500","#000000","#FFFFFF","#FFA800","#FFD635",
        "#00A368","#00CC78","#7EED56","#2450A4","#3690EA",
        "#51E9F4","#811E9F","#B44AC0","#FF99AA","#9C6926",
        "#6D482F","#BE0039","#FFB470","#515252","#898D90"
    };

    public static readonly byte[] Pixels = new byte[Width * Height];
    public static readonly object Gate = new();

    public static readonly Queue<PlaceUpdate> Recent = new();
    public static long LastTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static void Load()
    {
        try
        {
            Console.WriteLine($"[place] Using data path: {DataPath}");
            var dir = Path.GetDirectoryName(DataPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Console.WriteLine($"[place] Creating directory: {dir}");
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(DataPath))
            {
                var buf = File.ReadAllBytes(DataPath);
                if (buf.Length == Pixels.Length)
                {
                    Array.Copy(buf, Pixels, buf.Length);
                    Console.WriteLine("[place] Board loaded from data file.");
                }
                else
                {
                    Console.WriteLine($"[place] Existing file length {buf.Length} != {Pixels.Length}; starting fresh.");
                }
            }
            else
            {
                Console.WriteLine("[place] No existing board file; starting fresh.");
                Save(); // create it
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[place] Load error: " + ex);
        }
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(DataPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(DataPath, Pixels);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[place] Save error: " + ex.Message);
        }
    }

    public static void AddUpdate(int x, int y, byte color)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        LastTs = ts;
        Recent.Enqueue(new PlaceUpdate(x, y, color, ts));
        while (Recent.Count > 5000) Recent.Dequeue();
    }
}

// ---------- DTOs ----------
record EchoRequest(string Message);
record ScoreRequest(string Username, int? Value);
record Score
{
    public string Username { get; set; } = default!;
    public int Value { get; set; }
    public DateTimeOffset At { get; set; }
}
