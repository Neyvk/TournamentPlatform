using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Matches")
    ?? throw new InvalidOperationException("ConnectionStrings:Matches is required.");
builder.Services.AddDbContext<MatchesDbContext>(options => options.UseNpgsql(connectionString));
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<MatchesDbContext>().Database.EnsureCreatedAsync();
}

app.MapGet("/health", () => Results.Ok(new { service = "matches", status = "healthy" }));
app.MapGet("/api/v1/matches/{matchId:guid}", async (Guid matchId, MatchesDbContext db) =>
{
    var match = await db.Matches.FindAsync(matchId);
    return match is null ? Results.NotFound() : Results.Ok(match);
});
app.MapGet("/api/v1/matches/tournaments/{tournamentId:guid}", async (Guid tournamentId, MatchesDbContext db) =>
    Results.Ok(await db.Matches.Where(match => match.TournamentId == tournamentId).OrderBy(match => match.Round).ToListAsync()));

app.MapPost("/internal/matches/batch", async (CreateMatchesRequest request, MatchesDbContext db) =>
{
    var matches = request.Rounds.SelectMany((round, index) => round.Select(pair => new MatchRecord
    {
        Id = Guid.NewGuid(), TournamentId = request.TournamentId, Round = index + 1,
        PlayerAId = pair.PlayerAId, PlayerBId = pair.PlayerBId, Status = "scheduled"
    })).ToList();
    db.Matches.AddRange(matches);
    await db.SaveChangesAsync();
    return Results.Ok(matches);
});
app.Run();

public sealed record CreateMatchesRequest(Guid TournamentId, List<List<MatchPairRequest>> Rounds);
public sealed record MatchPairRequest(string PlayerAId, string? PlayerBId);
public sealed class MatchesDbContext(DbContextOptions<MatchesDbContext> options) : DbContext(options)
{
    public DbSet<MatchRecord> Matches => Set<MatchRecord>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<MatchRecord>(entity =>
    {
        entity.ToTable("Matches"); entity.HasKey(match => match.Id);
        entity.Property(match => match.PlayerAId).HasMaxLength(450).IsRequired();
        entity.Property(match => match.PlayerBId).HasMaxLength(450);
        entity.Property(match => match.Status).HasMaxLength(20).IsRequired();
    });
}
public sealed class MatchRecord
{
    public Guid Id { get; set; }
    public Guid TournamentId { get; set; }
    public int Round { get; set; }
    public required string PlayerAId { get; set; }
    public string? PlayerBId { get; set; }
    public required string Status { get; set; }
}
