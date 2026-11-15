using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Tournaments")
    ?? throw new InvalidOperationException("ConnectionStrings:Tournaments is required.");
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is required.");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Jwt:Key must contain at least 32 bytes.");
builder.Services.AddDbContext<TournamentsDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHttpClient("players", client => client.BaseAddress = new Uri(builder.Configuration["PlayersService:BaseUrl"] ?? "http://localhost:8082/"));
builder.Services.AddHttpClient("matches", client => client.BaseAddress = new Uri(builder.Configuration["MatchesService:BaseUrl"] ?? "http://localhost:8084/"));
var issuer = builder.Configuration["Jwt:Issuer"] ?? "TournamentPlatform.Identity";
var audience = builder.Configuration["Jwt:Audience"] ?? "TournamentPlatform";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TournamentsDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("""
        ALTER TABLE "Tournaments" ADD COLUMN IF NOT EXISTS "RoundCount" integer;
        ALTER TABLE "Tournaments" ADD COLUMN IF NOT EXISTS "Status" character varying(30) NOT NULL DEFAULT 'registration';
        ALTER TABLE "Tournaments" ADD COLUMN IF NOT EXISTS "OwnerId" character varying(450) NOT NULL DEFAULT 'legacy-unassigned';
        ALTER TABLE "Tournaments" ALTER COLUMN "OwnerId" DROP DEFAULT;
        CREATE TABLE IF NOT EXISTS "TournamentParticipants" (
            "TournamentId" uuid NOT NULL REFERENCES "Tournaments" ("Id") ON DELETE CASCADE,
            "PlayerId" character varying(450) NOT NULL,
            "RegisteredAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TournamentParticipants" PRIMARY KEY ("TournamentId", "PlayerId")
        );
        """);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { service = "tournaments", status = "healthy" }));

app.MapGet("/api/v1/tournaments", async (TournamentsDbContext db) =>
    Results.Ok(await db.Tournaments.OrderBy(tournament => tournament.CreatedAt).ToListAsync()));

app.MapGet("/api/v1/tournaments/{id:guid}", async (Guid id, TournamentsDbContext db) =>
{
    var tournament = await db.Tournaments.FindAsync(id);
    return tournament is null ? Results.NotFound() : Results.Ok(tournament);
});

app.MapPost("/api/v1/tournaments/{id:guid}/participants", async (
    Guid id, AddParticipantRequest request, HttpContext context, TournamentsDbContext db, IHttpClientFactory clients) =>
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
    var authenticatedPlayerId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    if (authenticatedPlayerId != playerId) return Results.Forbid();
    var response = await clients.CreateClient("players").GetAsync($"api/v1/players/{Uri.EscapeDataString(playerId)}");
    if (!response.IsSuccessStatusCode) return Results.NotFound(new { error = "Профиль игрока не найден." });
    if (await db.Participants.AnyAsync(entry => entry.TournamentId == id && entry.PlayerId == playerId))
        return Results.Conflict(new { error = "Игрок уже зарегистрирован." });
    db.Participants.Add(new TournamentParticipant { TournamentId = id, PlayerId = playerId, RegisteredAt = DateTimeOffset.UtcNow });
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/tournaments/{id}/participants/{playerId}", new { tournamentId = id, playerId });
}).RequireAuthorization();

app.MapPost("/api/v1/tournaments/{id:guid}/start", async (Guid id, HttpContext context, TournamentsDbContext db, IHttpClientFactory clients) =>
{
    var tournament = await db.Tournaments.FindAsync(id);
    if (tournament is null) return Results.NotFound();
    var userId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    if (tournament.OwnerId != userId) return Results.Forbid();
    if (tournament.Status != "registration") return Results.Conflict(new { error = "Турнир уже запущен." });
    var players = await db.Participants.Where(entry => entry.TournamentId == id).OrderBy(entry => entry.PlayerId).Select(entry => entry.PlayerId).ToListAsync();
    if (players.Count < 2) return Results.Conflict(new { error = "Для запуска нужно минимум два игрока." });
    var rounds = GenerateMatches(tournament.Format, players, tournament.RoundCount ?? 1);
    var response = await clients.CreateClient("matches").PostAsJsonAsync("internal/matches/batch", new { tournamentId = id, rounds });
    if (!response.IsSuccessStatusCode) return Results.Problem("Не удалось создать матчи.", statusCode: 503);
    tournament.Status = "in_progress";
    await db.SaveChangesAsync();
    return Results.Accepted($"/api/v1/tournaments/{id}/matches", new { tournamentId = id, status = tournament.Status, generatedRounds = rounds.Count });
}).RequireAuthorization();

app.MapPost("/internal/tournaments/{id:guid}/authorize-manager", async (
    Guid id, AuthorizeManagerRequest request, TournamentsDbContext db) =>
{
    var tournament = await db.Tournaments.FindAsync(id);
    if (tournament is null) return Results.NotFound();
    return tournament.OwnerId == request.UserId ? Results.NoContent() : Results.Forbid();
});

app.MapPost("/internal/tournaments/{id:guid}/matches/{matchId:guid}/completed", async (
    Guid id, Guid matchId, TournamentsDbContext db, IHttpClientFactory clients) =>
{
    var tournament = await db.Tournaments.FindAsync(id);
    if (tournament is null) return Results.NotFound();

    var matches = await clients.CreateClient("matches")
        .GetFromJsonAsync<List<MatchInfo>>($"api/v1/matches/tournaments/{id}");
    if (matches is null) return Results.Problem("Не удалось получить матчи турнира.", statusCode: 503);
    if (!matches.Any(match => match.Id == matchId && match.Status == "completed"))
        return Results.NotFound(new { error = "Завершённый матч не найден." });
    if (tournament.Status == "completed")
        return Results.Ok(new { status = tournament.Status });
    if (tournament.Status != "in_progress")
        return Results.Conflict(new { error = "Турнир не запущен." });

    var currentRound = matches.Max(match => match.Round);
    var roundMatches = matches.Where(match => match.Round == currentRound).ToList();
    if (roundMatches.Any(match => match.Status != "completed"))
        return Results.Ok(new { status = "waiting_for_round" });

    if (tournament.Format == "round_robin")
    {
        if (matches.All(match => match.Status == "completed"))
        {
            tournament.Status = "completed";
            await db.SaveChangesAsync();
        }
        return Results.Ok(new { status = tournament.Status });
    }

    if (tournament.Format == "olympic")
    {
        var advancingPlayers = roundMatches.Where(match => match.WinnerId is not null)
            .Select(match => match.WinnerId!).ToList();
        if (advancingPlayers.Count <= 1)
        {
            tournament.Status = "completed";
            await db.SaveChangesAsync();
            return Results.Ok(new { status = tournament.Status });
        }

        var nextRound = PairOlympicRound(advancingPlayers);
        var olympicCreateResponse = await clients.CreateClient("matches").PostAsJsonAsync("internal/matches/batch", new
        {
            tournamentId = id,
            startRound = currentRound + 1,
            rounds = new[] { nextRound }
        });
        if (!olympicCreateResponse.IsSuccessStatusCode)
            return Results.Problem("Не удалось создать следующий раунд.", statusCode: 503);
        return Results.Ok(new { status = "round_created", round = currentRound + 1 });
    }

    if (currentRound >= tournament.RoundCount.GetValueOrDefault(1))
    {
        tournament.Status = "completed";
        await db.SaveChangesAsync();
        return Results.Ok(new { status = tournament.Status });
    }

    var swissPlayers = await db.Participants
        .Where(entry => entry.TournamentId == id).Select(entry => entry.PlayerId).ToListAsync();
    var swissRound = PairSwissRound(matches, swissPlayers);
    var createResponse = await clients.CreateClient("matches").PostAsJsonAsync("internal/matches/batch", new
    {
        tournamentId = id,
        startRound = currentRound + 1,
        rounds = new[] { swissRound }
    });
    if (!createResponse.IsSuccessStatusCode)
        return Results.Problem("Не удалось создать следующий раунд.", statusCode: 503);
    return Results.Ok(new { status = "round_created", round = currentRound + 1 });
});

app.MapPost("/api/v1/tournaments", async (CreateTournamentRequest request, HttpContext context, TournamentsDbContext db) =>
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

    var ownerId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    if (string.IsNullOrWhiteSpace(ownerId)) return Results.Unauthorized();
    var tournament = new Tournament
    {
        Id = Guid.NewGuid(),
        Name = name,
        Format = format,
        OwnerId = ownerId,
        MaxPlayers = request.MaxPlayers,
        RoundCount = request.RoundCount,
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.Tournaments.Add(tournament);
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/tournaments/{tournament.Id}", tournament);
}).RequireAuthorization();

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
        rounds.Add(players.Take(byeCount).Select(player => new MatchPair(player, null))
            .Concat(PairOlympicRound(players.Skip(byeCount).ToList())).ToList());
    }
    else rounds.Add(PairSwissRound([], players));
    return rounds.Take(format == "swiss" ? 1 : int.MaxValue).ToList();
}

static List<MatchPair> PairOlympicRound(List<string> players) => players.Chunk(2)
    .Select(pair => new MatchPair(pair[0], pair.Length > 1 ? pair[1] : null)).ToList();

static List<MatchPair> PairSwissRound(List<MatchInfo> history, List<string> players)
{
    var points = players.ToDictionary(player => player, _ => 0);
    var opponents = players.ToDictionary(player => player, _ => new HashSet<string>());
    var playersWithBye = new HashSet<string>();
    foreach (var match in history)
    {
        if (match.PlayerBId is null)
        {
            playersWithBye.Add(match.PlayerAId);
            if (match.WinnerId == match.PlayerAId) points[match.PlayerAId]++;
            continue;
        }

        opponents[match.PlayerAId].Add(match.PlayerBId);
        opponents[match.PlayerBId].Add(match.PlayerAId);
        if (match.WinnerId is not null) points[match.WinnerId]++;
    }

    var remaining = players.OrderByDescending(player => points[player]).ThenBy(player => player).ToList();
    var pairs = new List<MatchPair>();
    if (remaining.Count % 2 != 0)
    {
        var bye = remaining.LastOrDefault(player => !playersWithBye.Contains(player)) ?? remaining[^1];
        pairs.Add(new MatchPair(bye, null));
        remaining.Remove(bye);
    }

    while (remaining.Count > 0)
    {
        var first = remaining[0];
        var opponentIndex = remaining.FindIndex(1, player => !opponents[first].Contains(player));
        if (opponentIndex < 0) opponentIndex = 1;
        var second = remaining[opponentIndex];
        pairs.Add(new MatchPair(first, second));
        remaining.RemoveAt(opponentIndex);
        remaining.RemoveAt(0);
    }
    return pairs;
}

public sealed record MatchPair(string PlayerAId, string? PlayerBId);
public sealed record MatchInfo(Guid Id, Guid TournamentId, int Round, string PlayerAId, string? PlayerBId,
    int? ScoreA, int? ScoreB, string? WinnerId, string Status);
public sealed record AuthorizeManagerRequest(string UserId);
public sealed record AddParticipantRequest(string? PlayerId);
public sealed record CreateTournamentRequest(string? Name, string? Format, int MaxPlayers, int? RoundCount);
public partial class Program;
