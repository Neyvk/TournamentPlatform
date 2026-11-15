using Microsoft.EntityFrameworkCore;

public sealed class TournamentsDbContext(DbContextOptions<TournamentsDbContext> options)
    : DbContext(options)
{
    public DbSet<Tournament> Tournaments => Set<Tournament>();
    public DbSet<TournamentParticipant> Participants => Set<TournamentParticipant>();

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
            entity.Property(tournament => tournament.MaxPlayers).IsRequired();
            entity.Property(tournament => tournament.CreatedAt).IsRequired();
        });
        modelBuilder.Entity<TournamentParticipant>(entity =>
        {
            entity.ToTable("TournamentParticipants");
            entity.HasKey(participant => new { participant.TournamentId, participant.PlayerId });
            entity.Property(participant => participant.PlayerId).HasMaxLength(450);
            entity.HasOne<Tournament>().WithMany().HasForeignKey(participant => participant.TournamentId)
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
    public int MaxPlayers { get; set; }
    public int? RoundCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "registration";
}

public sealed class TournamentParticipant
{
    public Guid TournamentId { get; set; }
    public required string PlayerId { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
}
