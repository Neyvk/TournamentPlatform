using Microsoft.EntityFrameworkCore;

public sealed class TournamentsDbContext(DbContextOptions<TournamentsDbContext> options)
    : DbContext(options)
{
    public DbSet<Tournament> Tournaments => Set<Tournament>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tournament>(entity =>
        {
            entity.ToTable("Tournaments");
            entity.HasKey(tournament => tournament.Id);
            entity.Property(tournament => tournament.Name).HasMaxLength(100).IsRequired();
            entity.Property(tournament => tournament.Format).HasMaxLength(20).IsRequired();
            entity.Property(tournament => tournament.MaxPlayers).IsRequired();
            entity.Property(tournament => tournament.CreatedAt).IsRequired();
        });
    }
}

public sealed class Tournament
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Format { get; set; }
    public int MaxPlayers { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
