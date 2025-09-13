using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Allow any origin while getting started (tighten later)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// For JSON options (camelCase)
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// In-memory storage (simple demo — replace with a DB later)
var scores = new List<Score>();

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();   // serves index.html if you add /wwwroot/index.html
app.UseStaticFiles();    // serves files from wwwroot

// ---------- Minimal endpoints ----------

app.UseDefaultFiles();
app.UseStaticFiles();

// Health check
app.MapGet("/health", () => new { ok = true, serverTime = DateTimeOffset.UtcNow });

// Echo back JSON
app.MapPost("/echo", (EchoRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Message))
        return Results.BadRequest(new { error = "message is required" });

    return Results.Ok(new { youSaid = req.Message, len = req.Message.Length });
});

// Submit a score
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

// Get top scores
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

app.Run();

// ---------- DTOs ----------
record EchoRequest(string Message);
record ScoreRequest(string Username, int? Value);
record Score
{
    public string Username { get; set; } = default!;
    public int Value { get; set; }
    public DateTimeOffset At { get; set; }
}
