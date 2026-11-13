using Microsoft.EntityFrameworkCore;

public sealed class PlayersDbContext(DbContextOptions<PlayersDbContext> options)
    : DbContext(options)
{
    public DbSet<PlayerProfile> Profiles => Set<PlayerProfile>();
    public DbSet<PlayerTeam> Teams => Set<PlayerTeam>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();

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

        modelBuilder.Entity<PlayerTeam>(entity =>
        {
            entity.ToTable("Teams");
            entity.HasKey(team => team.Id);
            entity.Property(team => team.Name).HasMaxLength(80).IsRequired();
            entity.Property(team => team.OwnerId).HasMaxLength(450).IsRequired();
            entity.Property(team => team.CreatedAt).IsRequired();
            entity.HasOne<PlayerProfile>().WithMany().HasForeignKey(team => team.OwnerId);
        });

        modelBuilder.Entity<TeamMember>(entity =>
        {
            entity.ToTable("TeamMembers");
            entity.HasKey(member => new { member.TeamId, member.UserId });
            entity.Property(member => member.UserId).HasMaxLength(450);
            entity.Property(member => member.JoinedAt).IsRequired();
            entity.HasOne<PlayerTeam>().WithMany().HasForeignKey(member => member.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<PlayerProfile>().WithMany().HasForeignKey(member => member.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class PlayerProfile
{
    public required string UserId { get; set; }
    public required string Nickname { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PlayerTeam
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string OwnerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TeamMember
{
    public Guid TeamId { get; set; }
    public required string UserId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
}
