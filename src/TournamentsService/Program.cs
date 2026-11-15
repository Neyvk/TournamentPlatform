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
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Tournaments' AND column_name = 'MaxPlayers')
               AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Tournaments' AND column_name = 'MaxTeams') THEN
                ALTER TABLE "Tournaments" RENAME COLUMN "MaxPlayers" TO "MaxTeams";
            END IF;
        END $$;
        ALTER TABLE "Tournaments" ADD COLUMN IF NOT EXISTS "MaxTeams" integer NOT NULL DEFAULT 2;
        ALTER TABLE "Tournaments" ALTER COLUMN "MaxTeams" DROP DEFAULT;
        ALTER TABLE "Tournaments" ADD COLUMN IF NOT EXISTS "RoundCount" integer;
        ALTER TABLE "Tournaments" ADD COLUMN IF NOT EXISTS "Status" character varying(30) NOT NULL DEFAULT 'registration';
        ALTER TABLE "Tournaments" ADD COLUMN IF NOT EXISTS "OwnerId" character varying(450) NOT NULL DEFAULT 'legacy-unassigned';
        ALTER TABLE "Tournaments" ALTER COLUMN "OwnerId" DROP DEFAULT;
        CREATE TABLE IF NOT EXISTS "TournamentTeams" (
            "TournamentId" uuid NOT NULL REFERENCES "Tournaments" ("Id") ON DELETE CASCADE,
            "TeamId" uuid NOT NULL,
            "RegisteredAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TournamentTeams" PRIMARY KEY ("TournamentId", "TeamId")
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

app.MapPost("/api/v1/tournaments/{id:guid}/teams", async (
    Guid id, AddParticipantRequest request, HttpContext context, TournamentsDbContext db, IHttpClientFactory clients) =>
{
    var tournament = await db.Tournaments.FindAsync(id);
    if (tournament is null) return Results.NotFound();
    if (tournament.Status != "registration") return Results.Conflict(new { error = "Регистрация закрыта." });
    var upperLimit = tournament.Format switch { "olympic" => 128, "round_robin" => 16, _ => 256 };
    if (tournament.MaxTeams > upperLimit) return Results.BadRequest(new { error = "Лимит команд превышает ограничение формата." });
    if (await db.Teams.CountAsync(entry => entry.TournamentId == id) >= tournament.MaxTeams)
        return Results.Conflict(new { error = "Достигнут лимит команд." });
    if (request.TeamId is null || request.TeamId == Guid.Empty)
        return Results.BadRequest(new { error = "teamId обязателен." });
    var userId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

    HttpResponseMessage teamResponse;
    try
    {
        teamResponse = await clients.CreateClient("players").GetAsync($"api/v1/teams/{request.TeamId}");
    }
    catch (HttpRequestException)
    {
        return Results.Problem("Сервис команд сейчас недоступен.", statusCode: 503);
    }
    if (teamResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
        return Results.NotFound(new { error = "Команда не найдена." });
    if (!teamResponse.IsSuccessStatusCode)
        return Results.Problem("Не удалось проверить команду.", statusCode: 503);
    var team = await teamResponse.Content.ReadFromJsonAsync<TeamInfo>();
    if (team is null) return Results.Problem("Не удалось прочитать данные команды.", statusCode: 503);
    if (team.OwnerId != userId) return Results.Forbid();
    if (await db.Teams.AnyAsync(entry => entry.TournamentId == id && entry.TeamId == request.TeamId))
        return Results.Conflict(new { error = "Команда уже зарегистрирована." });

    db.Teams.Add(new TournamentTeam { TournamentId = id, TeamId = request.TeamId.Value, RegisteredAt = DateTimeOffset.UtcNow });
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/tournaments/{id}/teams/{request.TeamId}", new { tournamentId = id, teamId = request.TeamId });
}).RequireAuthorization();

app.MapPost("/api/v1/tournaments/{id:guid}/start", async (Guid id, HttpContext context, TournamentsDbContext db, IHttpClientFactory clients) =>
{
    var tournament = await db.Tournaments.FindAsync(id);
    if (tournament is null) return Results.NotFound();
    var userId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    if (tournament.OwnerId != userId) return Results.Forbid();
    if (tournament.Status != "registration") return Results.Conflict(new { error = "Турнир уже запущен." });
    var teams = await db.Teams.Where(entry => entry.TournamentId == id).OrderBy(entry => entry.TeamId).Select(entry => entry.TeamId.ToString()).ToListAsync();
    if (teams.Count < 2) return Results.Conflict(new { error = "Для запуска нужны минимум две команды." });
    var rounds = GenerateMatches(tournament.Format, teams, tournament.RoundCount ?? 1);
    var response = await clients.CreateClient("matches").PostAsJsonAsync("internal/matches/batch", new { tournamentId = id, participantType = "team", rounds });
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
        var advancingTeams = roundMatches.Where(match => match.WinnerId is not null)
            .Select(match => match.WinnerId!).ToList();
        if (advancingTeams.Count <= 1)
        {
            tournament.Status = "completed";
            await db.SaveChangesAsync();
            return Results.Ok(new { status = tournament.Status });
        }

        var nextRound = PairOlympicRound(advancingTeams);
        var olympicCreateResponse = await clients.CreateClient("matches").PostAsJsonAsync("internal/matches/batch", new
        {
            tournamentId = id,
            participantType = "team",
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

    var swissTeams = await db.Teams
        .Where(entry => entry.TournamentId == id).Select(entry => entry.TeamId.ToString()).ToListAsync();
    var swissRound = PairSwissRound(matches, swissTeams);
    var createResponse = await clients.CreateClient("matches").PostAsJsonAsync("internal/matches/batch", new
    {
        tournamentId = id,
        participantType = "team",
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
    if (request.MaxTeams < 2 || request.MaxTeams > upperLimit)
        return Results.BadRequest(new { error = $"Для формата {format} допустимо от 2 до {upperLimit} команд." });
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
        MaxTeams = request.MaxTeams,
        RoundCount = request.RoundCount,
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.Tournaments.Add(tournament);
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/tournaments/{tournament.Id}", tournament);
}).RequireAuthorization();

app.Run();

static List<List<MatchPair>> GenerateMatches(string format, List<string> teams, int roundCount)
{
    var rounds = new List<List<MatchPair>>();
    if (format == "round_robin")
    {
        var rotating = teams.Select((id, index) => (id, index)).ToList();
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
        while (bracketSize < teams.Count) bracketSize *= 2;
        var byeCount = bracketSize - teams.Count;
        rounds.Add(teams.Take(byeCount).Select(team => new MatchPair(team, null))
            .Concat(PairOlympicRound(teams.Skip(byeCount).ToList())).ToList());
    }
    else rounds.Add(PairSwissRound([], teams));
    return rounds.Take(format == "swiss" ? 1 : int.MaxValue).ToList();
}

static List<MatchPair> PairOlympicRound(List<string> teams) => teams.Chunk(2)
    .Select(pair => new MatchPair(pair[0], pair.Length > 1 ? pair[1] : null)).ToList();

static List<MatchPair> PairSwissRound(List<MatchInfo> history, List<string> teams)
{
    var points = teams.ToDictionary(team => team, _ => 0);
    var opponents = teams.ToDictionary(team => team, _ => new HashSet<string>());
    var teamsWithBye = new HashSet<string>();
    foreach (var match in history)
    {
        if (match.ParticipantBId is null)
        {
            teamsWithBye.Add(match.ParticipantAId);
            if (match.WinnerId == match.ParticipantAId) points[match.ParticipantAId]++;
            continue;
        }

        opponents[match.ParticipantAId].Add(match.ParticipantBId);
        opponents[match.ParticipantBId].Add(match.ParticipantAId);
        if (match.WinnerId is not null) points[match.WinnerId]++;
    }

    var remaining = teams.OrderByDescending(team => points[team]).ThenBy(team => team).ToList();
    var pairs = new List<MatchPair>();
    if (remaining.Count % 2 != 0)
    {
        var bye = remaining.LastOrDefault(team => !teamsWithBye.Contains(team)) ?? remaining[^1];
        pairs.Add(new MatchPair(bye, null));
        remaining.Remove(bye);
    }

    while (remaining.Count > 0)
    {
        var first = remaining[0];
        var opponentIndex = remaining.FindIndex(1, team => !opponents[first].Contains(team));
        if (opponentIndex < 0) opponentIndex = 1;
        var second = remaining[opponentIndex];
        pairs.Add(new MatchPair(first, second));
        remaining.RemoveAt(opponentIndex);
        remaining.RemoveAt(0);
    }
    return pairs;
}

public sealed record MatchPair(string TeamAId, string? TeamBId);
public sealed record MatchInfo(Guid Id, Guid TournamentId, int Round, string ParticipantType,
    string ParticipantAId, string? ParticipantBId, int? ScoreA, int? ScoreB, string? WinnerId, string Status);
public sealed record AuthorizeManagerRequest(string UserId);
public sealed record AddParticipantRequest(Guid? TeamId);
public sealed record TeamInfo(Guid Id, string Name, string OwnerId, string[] Members);
public sealed record CreateTournamentRequest(string? Name, string? Format, int MaxTeams, int? RoundCount);
public partial class Program;
