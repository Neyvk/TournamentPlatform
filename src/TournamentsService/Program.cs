using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Tournaments")
    ?? throw new InvalidOperationException("ConnectionStrings:Tournaments is required.");
builder.Services.AddDbContext<TournamentsDbContext>(options => options.UseNpgsql(connectionString));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TournamentsDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapGet("/health", () => Results.Ok(new { service = "tournaments", status = "healthy" }));

app.MapGet("/api/v1/tournaments", async (TournamentsDbContext db) =>
    Results.Ok(await db.Tournaments.OrderBy(tournament => tournament.CreatedAt).ToListAsync()));

app.MapGet("/api/v1/tournaments/{id:guid}", async (Guid id, TournamentsDbContext db) =>
{
    var tournament = await db.Tournaments.FindAsync(id);
    return tournament is null ? Results.NotFound() : Results.Ok(tournament);
});

app.MapPost("/api/v1/tournaments", async (CreateTournamentRequest request, TournamentsDbContext db) =>
{
    var name = request.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        return Results.BadRequest(new { error = "Название турнира должно содержать от 1 до 100 символов." });

    var format = request.Format?.Trim().ToLowerInvariant();
    if (format is not ("olympic" or "round_robin" or "swiss"))
        return Results.BadRequest(new { error = "Формат должен быть olympic, round_robin или swiss." });

    if (request.MaxPlayers < 2 || request.MaxPlayers > 1024)
        return Results.BadRequest(new { error = "Количество игроков должно быть от 2 до 1024." });

    var tournament = new Tournament
    {
        Id = Guid.NewGuid(),
        Name = name,
        Format = format,
        MaxPlayers = request.MaxPlayers,
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.Tournaments.Add(tournament);
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/tournaments/{tournament.Id}", tournament);
});

app.Run();

public sealed record CreateTournamentRequest(string? Name, string? Format, int MaxPlayers);
public partial class Program;
