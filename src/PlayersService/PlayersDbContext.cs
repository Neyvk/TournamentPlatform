using Microsoft.EntityFrameworkCore;

public sealed class PlayersDbContext(DbContextOptions<PlayersDbContext> options)
    : DbContext(options)
{
    public DbSet<PlayerProfile> Profiles => Set<PlayerProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlayerProfile>(entity =>
        {
            entity.ToTable("PlayerProfiles");
            entity.HasKey(profile => profile.UserId);
            entity.Property(profile => profile.UserId).HasMaxLength(450);
            entity.Property(profile => profile.Nickname).HasMaxLength(80).IsRequired();
            entity.HasIndex(profile => profile.Nickname).IsUnique();
            entity.Property(profile => profile.CreatedAt).IsRequired();
        });
    }
}

public sealed class PlayerProfile
{
    public required string UserId { get; set; }
    public required string Nickname { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
