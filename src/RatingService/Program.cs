using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using TournamentPlatform.Messaging;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Rating")
    ?? throw new InvalidOperationException("ConnectionStrings:Rating is required.");
builder.Services.AddDbContext<RatingDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHttpClient("self", client => client.BaseAddress = new Uri(builder.Configuration["RatingService:SelfBaseUrl"] ?? "http://localhost:8085/"));
builder.Services.AddHostedService<RatingMatchCompletedConsumer>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<RatingDbContext>().Database.EnsureCreatedAsync();
}

app.MapGet("/health", () => Results.Ok(new { service = "rating", status = "healthy" }));

app.MapGet("/api/v1/ratings/teams/{teamId:guid}", async (Guid teamId, RatingDbContext db) =>
{
    var rating = await db.Ratings.FindAsync("team", teamId.ToString());
    return rating is null
        ? Results.NotFound()
        : Results.Ok(new { teamId = rating.ParticipantId, value = rating.Value,
            matchesPlayed = rating.MatchesPlayed, updatedAt = rating.UpdatedAt });
});

app.MapGet("/api/v1/ratings/leaderboard", async (int? limit, RatingDbContext db) =>
{
    var resultLimit = limit ?? 50;
    if (resultLimit is < 1 or > 100)
        return Results.BadRequest(new { error = "limit должен быть от 1 до 100." });

    var ratings = await db.Ratings.Where(rating => rating.ParticipantType == "team")
        .OrderByDescending(rating => rating.Value).ThenBy(rating => rating.ParticipantId)
        .Take(resultLimit).ToListAsync();
    return Results.Ok(ratings.Select((rating, index) => new
    {
        teamId = rating.ParticipantId,
        value = rating.Value,
        matchesPlayed = rating.MatchesPlayed,
        rank = index + 1,
        updatedAt = rating.UpdatedAt
    }));
});

app.MapPost("/internal/ratings/matches", async (CompletedTeamMatchRatingRequest request, RatingDbContext db) =>
{
    if (request.MatchId == Guid.Empty || string.IsNullOrWhiteSpace(request.TeamAId)
        || string.IsNullOrWhiteSpace(request.TeamBId) || request.TeamAId == request.TeamBId
        || request.ScoreA < 0 || request.ScoreB < 0 || request.ScoreA == request.ScoreB)
        return Results.BadRequest(new { error = "Для расчёта нужны две разные команды и корректный счёт с победителем." });

    if (await db.ProcessedMatches.AnyAsync(match => match.MatchId == request.MatchId))
        return Results.Ok(new { processed = false, message = "Матч уже учтён." });

    await using var transaction = await db.Database.BeginTransactionAsync();
    var ratingA = await db.Ratings.FindAsync("team", request.TeamAId)
        ?? new ParticipantRating { ParticipantType = "team", ParticipantId = request.TeamAId, Value = 1000 };
    var ratingB = await db.Ratings.FindAsync("team", request.TeamBId)
        ?? new ParticipantRating { ParticipantType = "team", ParticipantId = request.TeamBId, Value = 1000 };

    var expectedA = 1.0 / (1.0 + Math.Pow(10, (ratingB.Value - ratingA.Value) / 400.0));
    var actualA = request.ScoreA > request.ScoreB ? 1.0 : 0.0;
    var change = (int)Math.Round(32 * (actualA - expectedA), MidpointRounding.AwayFromZero);
    ratingA.Value += change;
    ratingB.Value -= change;
    ratingA.MatchesPlayed++;
    ratingB.MatchesPlayed++;
    ratingA.UpdatedAt = DateTimeOffset.UtcNow;
    ratingB.UpdatedAt = ratingA.UpdatedAt;

    if (db.Entry(ratingA).State == EntityState.Detached) db.Ratings.Add(ratingA);
    if (db.Entry(ratingB).State == EntityState.Detached) db.Ratings.Add(ratingB);
    db.ProcessedMatches.Add(new ProcessedRatingMatch { MatchId = request.MatchId, ProcessedAt = DateTimeOffset.UtcNow });
    await db.SaveChangesAsync();
    await transaction.CommitAsync();

    return Results.Ok(new
    {
        processed = true,
        teamA = new { id = ratingA.ParticipantId, value = ratingA.Value },
        teamB = new { id = ratingB.ParticipantId, value = ratingB.Value }
    });
});

app.Run();

public sealed record CompletedTeamMatchRatingRequest(Guid MatchId, string TeamAId, string TeamBId, int ScoreA, int ScoreB);

public sealed class RatingDbContext(DbContextOptions<RatingDbContext> options) : DbContext(options)
{
    public DbSet<ParticipantRating> Ratings => Set<ParticipantRating>();
    public DbSet<ProcessedRatingMatch> ProcessedMatches => Set<ProcessedRatingMatch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ParticipantRating>(entity =>
        {
            entity.ToTable("Ratings");
            entity.HasKey(rating => new { rating.ParticipantType, rating.ParticipantId });
            entity.Property(rating => rating.ParticipantType).HasMaxLength(20);
            entity.Property(rating => rating.ParticipantId).HasMaxLength(450);
        });
        modelBuilder.Entity<ProcessedRatingMatch>(entity =>
        {
            entity.ToTable("ProcessedMatches");
            entity.HasKey(match => match.MatchId);
        });
    }
}

public sealed class ParticipantRating
{
    public required string ParticipantType { get; set; }
    public required string ParticipantId { get; set; }
    public int Value { get; set; }
    public int MatchesPlayed { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProcessedRatingMatch
{
    public Guid MatchId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

public sealed class RatingMatchCompletedConsumer(
    IConfiguration configuration,
    ILogger<RatingMatchCompletedConsumer> logger,
    IHttpClientFactory clients)
    : MatchCompletedConsumer(configuration, logger, RabbitMqTopology.RatingQueue)
{
    protected override async Task HandleAsync(MatchCompletedEvent message, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient("self").PostAsJsonAsync(
            "internal/ratings/matches",
            new
            {
                matchId = message.MatchId,
                teamAId = message.TeamAId,
                teamBId = message.TeamBId,
                scoreA = message.ScoreA,
                scoreB = message.ScoreB
            }, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Rating Service returned {(int)response.StatusCode} for MatchCompleted.");
    }
}
