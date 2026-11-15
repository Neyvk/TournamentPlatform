using Microsoft.EntityFrameworkCore;

public sealed class TournamentsDbContext(DbContextOptions<TournamentsDbContext> options)
    : DbContext(options)
{
    public DbSet<Tournament> Tournaments => Set<Tournament>();
    public DbSet<TournamentTeam> Teams => Set<TournamentTeam>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tournament>(entity =>
        {
            entity.ToTable("Tournaments");
            entity.HasKey(tournament => tournament.Id);
            entity.Property(tournament => tournament.Name).HasMaxLength(100).IsRequired();
            entity.Property(tournament => tournament.Format).HasMaxLength(20).IsRequired();
            entity.Property(tournament => tournament.OwnerId).HasMaxLength(450).IsRequired();
            entity.HasIndex(tournament => tournament.OwnerId);
            entity.Property(tournament => tournament.MaxTeams).IsRequired();
            entity.Property(tournament => tournament.CreatedAt).IsRequired();
        });
        modelBuilder.Entity<TournamentTeam>(entity =>
        {
            entity.ToTable("TournamentTeams");
            entity.HasKey(team => new { team.TournamentId, team.TeamId });
            entity.HasOne<Tournament>().WithMany().HasForeignKey(team => team.TournamentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class Tournament
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Format { get; set; }
    public required string OwnerId { get; set; }
    public int MaxTeams { get; set; }
    public int? RoundCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "registration";
}

public sealed class TournamentTeam
{
    public Guid TournamentId { get; set; }
    public Guid TeamId { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
}
