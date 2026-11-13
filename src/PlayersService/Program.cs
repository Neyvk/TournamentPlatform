using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Players")
    ?? throw new InvalidOperationException("ConnectionStrings:Players is required.");

builder.Services.AddDbContext<PlayersDbContext>(options => options.UseNpgsql(connectionString));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PlayersDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapGet("/health", () => Results.Ok(new { service = "players", status = "healthy" }));

app.MapGet("/api/v1/players", async (PlayersDbContext db) =>
    Results.Ok(await db.Profiles.OrderBy(profile => profile.Nickname).ToListAsync()));

app.MapGet("/api/v1/players/{userId}", async (string userId, PlayersDbContext db) =>
{
    var profile = await db.Profiles.FindAsync(userId);
    return profile is null ? Results.NotFound() : Results.Ok(profile);
});

app.MapPost("/internal/player-profiles", async (CreatePlayerProfileRequest request, PlayersDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.Nickname))
        return Results.BadRequest(new { error = "UserId и nickname обязательны." });

    if (await db.Profiles.AnyAsync(profile => profile.UserId == request.UserId))
        return Results.Conflict(new { error = "Профиль для пользователя уже создан." });

    var profile = new PlayerProfile
    {
        UserId = request.UserId,
        Nickname = request.Nickname.Trim(),
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.Profiles.Add(profile);
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/players/{profile.UserId}", profile);
});

app.MapGet("/api/v1/teams", () => Results.Ok(Array.Empty<object>()));
app.Run();

public sealed record CreatePlayerProfileRequest(string UserId, string Nickname);
public partial class Program;
