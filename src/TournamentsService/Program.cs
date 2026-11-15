using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Tournaments")
    ?? throw new InvalidOperationException("ConnectionStrings:Tournaments is required.");
builder.Services.AddDbContext<TournamentsDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHttpClient("players", client => client.BaseAddress = new Uri(builder.Configuration["PlayersService:BaseUrl"] ?? "http://localhost:8082/"));
builder.Services.AddHttpClient("matches", client => client.BaseAddress = new Uri(builder.Configuration["MatchesService:BaseUrl"] ?? "http://localhost:8084/"));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TournamentsDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("""
        ALTER TABLE "Tournaments" ADD COLUMN IF NOT EXISTS "RoundCount" integer;
        ALTER TABLE "Tournaments" ADD COLUMN IF NOT EXISTS "Status" character varying(30) NOT NULL DEFAULT 'registration';
        CREATE TABLE IF NOT EXISTS "TournamentParticipants" (
            "TournamentId" uuid NOT NULL REFERENCES "Tournaments" ("Id") ON DELETE CASCADE,
            "PlayerId" character varying(450) NOT NULL,
            "RegisteredAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TournamentParticipants" PRIMARY KEY ("TournamentId", "PlayerId")
        );
        """);
}

app.MapGet("/health", () => Results.Ok(new { service = "tournaments", status = "healthy" }));

app.MapGet("/api/v1/tournaments", async (TournamentsDbContext db) =>
    Results.Ok(await db.Tournaments.OrderBy(tournament => tournament.CreatedAt).ToListAsync()));

app.MapGet("/api/v1/tournaments/{id:guid}", async (Guid id, TournamentsDbContext db) =>
{
    var tournament = await db.Tournaments.FindAsync(id);
    return tournament is null ? Results.NotFound() : Results.Ok(tournament);
});

app.MapPost("/api/v1/tournaments/{id:guid}/participants", async (
    Guid id, AddParticipantRequest request, TournamentsDbContext db, IHttpClientFactory clients) =>
{
    var tournament = await db.Tournaments.FindAsync(id);
    if (tournament is null) return Results.NotFound();
    if (tournament.Status != "registration") return Results.Conflict(new { error = "Регистрация закрыта." });
    var upperLimit = tournament.Format switch { "olympic" => 128, "round_robin" => 16, _ => 256 };
    if (tournament.MaxPlayers > upperLimit) return Results.BadRequest(new { error = "Лимит участников превышает ограничение формата." });
    if (await db.Participants.CountAsync(entry => entry.TournamentId == id) >= tournament.MaxPlayers)
        return Results.Conflict(new { error = "Достигнут лимит игроков." });
    var playerId = request.PlayerId?.Trim();
    if (string.IsNullOrWhiteSpace(playerId)) return Results.BadRequest(new { error = "playerId обязателен." });
    var response = await clients.CreateClient("players").GetAsync($"api/v1/players/{Uri.EscapeDataString(playerId)}");
    if (!response.IsSuccessStatusCode) return Results.NotFound(new { error = "Профиль игрока не найден." });
    if (await db.Participants.AnyAsync(entry => entry.TournamentId == id && entry.PlayerId == playerId))
        return Results.Conflict(new { error = "Игрок уже зарегистрирован." });
    db.Participants.Add(new TournamentParticipant { TournamentId = id, PlayerId = playerId, RegisteredAt = DateTimeOffset.UtcNow });
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/tournaments/{id}/participants/{playerId}", new { tournamentId = id, playerId });
});

app.MapPost("/api/v1/tournaments/{id:guid}/start", async (Guid id, TournamentsDbContext db, IHttpClientFactory clients) =>
{
    var tournament = await db.Tournaments.FindAsync(id);
    if (tournament is null) return Results.NotFound();
    if (tournament.Status != "registration") return Results.Conflict(new { error = "Турнир уже запущен." });
    var players = await db.Participants.Where(entry => entry.TournamentId == id).OrderBy(entry => entry.PlayerId).Select(entry => entry.PlayerId).ToListAsync();
    if (players.Count < 2) return Results.Conflict(new { error = "Для запуска нужно минимум два игрока." });
    var rounds = GenerateMatches(tournament.Format, players, tournament.RoundCount ?? 1);
    var response = await clients.CreateClient("matches").PostAsJsonAsync("internal/matches/batch", new { tournamentId = id, rounds });
    if (!response.IsSuccessStatusCode) return Results.Problem("Не удалось создать матчи.", statusCode: 503);
    tournament.Status = "in_progress";
    await db.SaveChangesAsync();
    return Results.Accepted($"/api/v1/tournaments/{id}/matches", new { tournamentId = id, status = tournament.Status, generatedRounds = rounds.Count });
});

app.MapPost("/api/v1/tournaments", async (CreateTournamentRequest request, TournamentsDbContext db) =>
{
    var name = request.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        return Results.BadRequest(new { error = "Название турнира должно содержать от 1 до 100 символов." });

    var format = request.Format?.Trim().ToLowerInvariant();
    if (format is not ("olympic" or "round_robin" or "swiss"))
        return Results.BadRequest(new { error = "Формат должен быть olympic, round_robin или swiss." });

    var upperLimit = format switch { "olympic" => 128, "round_robin" => 16, _ => 256 };
    if (request.MaxPlayers < 2 || request.MaxPlayers > upperLimit)
        return Results.BadRequest(new { error = $"Для формата {format} допустимо от 2 до {upperLimit} игроков." });
    if (format == "swiss" && (request.RoundCount is null or < 1 or > 20))
        return Results.BadRequest(new { error = "Для швейцарской системы укажите от 1 до 20 раундов." });
    if (format != "swiss" && request.RoundCount is not null)
        return Results.BadRequest(new { error = "Число раундов указывается только для швейцарской системы." });

    var tournament = new Tournament
    {
        Id = Guid.NewGuid(),
        Name = name,
        Format = format,
        MaxPlayers = request.MaxPlayers,
        RoundCount = request.RoundCount,
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.Tournaments.Add(tournament);
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/tournaments/{tournament.Id}", tournament);
});

app.Run();

static List<List<MatchPair>> GenerateMatches(string format, List<string> players, int roundCount)
{
    var rounds = new List<List<MatchPair>>();
    if (format == "round_robin")
    {
        var rotating = players.Select((id, index) => (id, index)).ToList();
        if (rotating.Count % 2 != 0) rotating.Add((null!, -1));
        for (var r = 0; r < rotating.Count - 1; r++)
        {
            var pairs = new List<MatchPair>();
            for (var i = 0; i < rotating.Count / 2; i++)
            {
                var a = rotating[i].id; var b = rotating[rotating.Count - 1 - i].id;
                if (a is not null && b is not null) pairs.Add(new MatchPair(a, b));
            }
            rounds.Add(pairs);
            rotating = [rotating[0], rotating[^1], .. rotating.Skip(1).SkipLast(1)];
        }
    }
    else if (format == "olympic")
    {
        var bracketSize = 1;
        while (bracketSize < players.Count) bracketSize *= 2;
        var byeCount = bracketSize - players.Count;
        rounds.Add(players.Skip(byeCount).Chunk(2)
            .Select(pair => new MatchPair(pair[0], pair.Length > 1 ? pair[1] : null)).ToList());
    }
    else rounds.Add(players.Chunk(2).Select(pair => new MatchPair(pair[0], pair.Length > 1 ? pair[1] : null)).ToList());
    return rounds.Take(format == "swiss" ? 1 : int.MaxValue).ToList();
}

public sealed record MatchPair(string PlayerAId, string? PlayerBId);
public sealed record AddParticipantRequest(string? PlayerId);
public sealed record CreateTournamentRequest(string? Name, string? Format, int MaxPlayers, int? RoundCount);
public partial class Program;
