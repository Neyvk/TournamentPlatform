using Microsoft.EntityFrameworkCore;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Matches")
    ?? throw new InvalidOperationException("ConnectionStrings:Matches is required.");
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is required.");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Jwt:Key must contain at least 32 bytes.");
builder.Services.AddDbContext<MatchesDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHttpClient("tournaments", client => client.BaseAddress = new Uri(builder.Configuration["TournamentsService:BaseUrl"] ?? "http://localhost:8083/"));
builder.Services.AddHttpClient("rating", client => client.BaseAddress = new Uri(builder.Configuration["RatingService:BaseUrl"] ?? "http://localhost:8085/"));
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
    var db = scope.ServiceProvider.GetRequiredService<MatchesDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("""
        ALTER TABLE "Matches" ADD COLUMN IF NOT EXISTS "ScoreA" integer;
        ALTER TABLE "Matches" ADD COLUMN IF NOT EXISTS "ScoreB" integer;
        ALTER TABLE "Matches" ADD COLUMN IF NOT EXISTS "CompletedAt" timestamp with time zone;
        ALTER TABLE "Matches" ADD COLUMN IF NOT EXISTS "ParticipantType" character varying(20) NOT NULL DEFAULT 'player';
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Matches' AND column_name = 'PlayerAId')
               AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Matches' AND column_name = 'ParticipantAId') THEN
                ALTER TABLE "Matches" RENAME COLUMN "PlayerAId" TO "ParticipantAId";
            END IF;
            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Matches' AND column_name = 'PlayerBId')
               AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Matches' AND column_name = 'ParticipantBId') THEN
                ALTER TABLE "Matches" RENAME COLUMN "PlayerBId" TO "ParticipantBId";
            END IF;
        END $$;
        ALTER TABLE "Matches" ALTER COLUMN "ParticipantType" DROP DEFAULT;
        """);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { service = "matches", status = "healthy" }));
app.MapGet("/api/v1/matches/{matchId:guid}", async (Guid matchId, MatchesDbContext db) =>
{
    var match = await db.Matches.FindAsync(matchId);
    return match is null || match.ParticipantType != "team" ? Results.NotFound() : Results.Ok(match);
});
app.MapGet("/api/v1/matches/tournaments/{tournamentId:guid}", async (Guid tournamentId, MatchesDbContext db) =>
    Results.Ok(await db.Matches.Where(match => match.TournamentId == tournamentId && match.ParticipantType == "team")
        .OrderBy(match => match.Round).ToListAsync()));

app.MapPut("/api/v1/matches/{matchId:guid}/result", async (
    Guid matchId, SetMatchResultRequest request, HttpContext context, MatchesDbContext db, IHttpClientFactory clients) =>
{
    if (request.ScoreA < 0 || request.ScoreB < 0 || request.ScoreA == request.ScoreB)
        return Results.BadRequest(new { error = "Счёт должен быть неотрицательным и определять победителя." });

    var match = await db.Matches.FindAsync(matchId);
    if (match is null) return Results.NotFound();
    if (match.ParticipantType != "team") return Results.NotFound();
    var userId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
    HttpResponseMessage accessResponse;
    try
    {
        accessResponse = await clients.CreateClient("tournaments")
            .PostAsJsonAsync($"internal/tournaments/{match.TournamentId}/authorize-manager", new { userId });
    }
    catch (HttpRequestException)
    {
        return Results.Problem("Сейчас не удалось проверить права управления турниром.", statusCode: 503);
    }
    if (accessResponse.StatusCode == System.Net.HttpStatusCode.NotFound) return Results.NotFound();
    if (accessResponse.StatusCode == System.Net.HttpStatusCode.Forbidden) return Results.Forbid();
    if (!accessResponse.IsSuccessStatusCode)
        return Results.Problem("Не удалось проверить права управления турниром.", statusCode: 503);
    if (match.ParticipantBId is null) return Results.Conflict(new { error = "У технической победы нельзя изменить результат." });
    if (match.Status == "completed" && (match.ScoreA != request.ScoreA || match.ScoreB != request.ScoreB))
        return Results.Conflict(new { error = "Результат матча уже зафиксирован." });

    if (match.Status != "completed")
    {
        match.ScoreA = request.ScoreA;
        match.ScoreB = request.ScoreB;
        match.WinnerId = request.ScoreA > request.ScoreB ? match.ParticipantAId : match.ParticipantBId;
        match.Status = "completed";
        match.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    try
    {
        var response = await clients.CreateClient("tournaments")
            .PostAsJsonAsync($"internal/tournaments/{match.TournamentId}/matches/{match.Id}/completed", new { matchId = match.Id });
        if (!response.IsSuccessStatusCode)
            return Results.Problem("Результат сохранён, но турнир пока не удалось продолжить. Повтори отправку того же результата.", statusCode: 503);
    }
    catch (HttpRequestException)
    {
        return Results.Problem("Результат сохранён, но сервис турниров сейчас недоступен. Повтори отправку того же результата.", statusCode: 503);
    }

    try
    {
        if (match.ParticipantType == "team")
        {
            var ratingResponse = await clients.CreateClient("rating").PostAsJsonAsync("internal/ratings/matches", new
            {
                matchId = match.Id,
                teamAId = match.ParticipantAId,
                teamBId = match.ParticipantBId,
                scoreA = match.ScoreA,
                scoreB = match.ScoreB
            });
            if (!ratingResponse.IsSuccessStatusCode)
                return Results.Problem("Результат сохранён, но рейтинг не обновлён. Повтори отправку того же результата.", statusCode: 503);
        }
    }
    catch (HttpRequestException)
    {
        return Results.Problem("Результат сохранён, но сервис рейтинга сейчас недоступен. Повтори отправку того же результата.", statusCode: 503);
    }

    return Results.Ok(match);
}).RequireAuthorization();

app.MapPost("/internal/matches/batch", async (CreateMatchesRequest request, MatchesDbContext db) =>
{
    if (request.ParticipantType != "team")
        return Results.BadRequest(new { error = "Турниры поддерживают только команды." });
    var matches = request.Rounds.SelectMany((round, index) => round.Select(pair => new MatchRecord
    {
        Id = Guid.NewGuid(), TournamentId = request.TournamentId, Round = request.StartRound + index,
        ParticipantType = request.ParticipantType,
        ParticipantAId = pair.TeamAId, ParticipantBId = pair.TeamBId,
        Status = pair.TeamBId is null ? "completed" : "scheduled",
        WinnerId = pair.TeamBId is null ? pair.TeamAId : null,
        CompletedAt = pair.TeamBId is null ? DateTimeOffset.UtcNow : null
    })).ToList();
    db.Matches.AddRange(matches);
    await db.SaveChangesAsync();
    return Results.Ok(matches);
});
app.Run();

public sealed record CreateMatchesRequest(Guid TournamentId, string ParticipantType, List<List<MatchPairRequest>> Rounds, int StartRound = 1);
public sealed record MatchPairRequest(string TeamAId, string? TeamBId);
public sealed record SetMatchResultRequest(int ScoreA, int ScoreB);
public sealed class MatchesDbContext(DbContextOptions<MatchesDbContext> options) : DbContext(options)
{
    public DbSet<MatchRecord> Matches => Set<MatchRecord>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<MatchRecord>(entity =>
    {
        entity.ToTable("Matches"); entity.HasKey(match => match.Id);
        entity.Property(match => match.ParticipantType).HasMaxLength(20).IsRequired();
        entity.Property(match => match.ParticipantAId).HasMaxLength(450).IsRequired();
        entity.Property(match => match.ParticipantBId).HasMaxLength(450);
        entity.Property(match => match.WinnerId).HasMaxLength(450);
        entity.Property(match => match.Status).HasMaxLength(20).IsRequired();
    });
}
public sealed class MatchRecord
{
    public Guid Id { get; set; }
    public Guid TournamentId { get; set; }
    public int Round { get; set; }
    public required string ParticipantType { get; set; }
    public required string ParticipantAId { get; set; }
    public string? ParticipantBId { get; set; }
    public int? ScoreA { get; set; }
    public int? ScoreB { get; set; }
    public string? WinnerId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required string Status { get; set; }
}
