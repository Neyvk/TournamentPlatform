using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Players")
    ?? throw new InvalidOperationException("ConnectionStrings:Players is required.");
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is required.");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Jwt:Key must contain at least 32 bytes.");

builder.Services.AddDbContext<PlayersDbContext>(options => options.UseNpgsql(connectionString));
var issuer = builder.Configuration["Jwt:Issuer"] ?? "TournamentPlatform.Identity";
var audience = builder.Configuration["Jwt:Audience"] ?? "TournamentPlatform";
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
    var db = scope.ServiceProvider.GetRequiredService<PlayersDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "Teams" (
            "Id" uuid PRIMARY KEY,
            "Name" character varying(80) NOT NULL,
            "OwnerId" character varying(450) NOT NULL REFERENCES "PlayerProfiles" ("UserId"),
            "CreatedAt" timestamp with time zone NOT NULL
        );
        CREATE TABLE IF NOT EXISTS "TeamMembers" (
            "TeamId" uuid NOT NULL REFERENCES "Teams" ("Id") ON DELETE CASCADE,
            "UserId" character varying(450) NOT NULL REFERENCES "PlayerProfiles" ("UserId") ON DELETE CASCADE,
            "JoinedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_TeamMembers" PRIMARY KEY ("TeamId", "UserId")
        );
        """);
    await DemoDataSeeder.EnsureAsync(db);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { service = "players", status = "healthy" }));

app.MapGet("/api/v1/players", async (PlayersDbContext db) =>
    Results.Ok(await db.Profiles.OrderBy(profile => profile.Nickname).ToListAsync()));

app.MapGet("/api/v1/players/{userId}", async (string userId, PlayersDbContext db) =>
{
    var profile = await db.Profiles.FindAsync(userId);
    return profile is null ? Results.NotFound() : Results.Ok(profile);
});

app.MapPost("/internal/player-profiles", async (CreatePlayerProfileRequest request, PlayersDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.Nickname))
        return Results.BadRequest(new { error = "UserId и nickname обязательны." });

    if (await db.Profiles.AnyAsync(profile => profile.UserId == request.UserId))
        return Results.Conflict(new { error = "Профиль для пользователя уже создан." });

    var profile = new PlayerProfile
    {
        UserId = request.UserId,
        Nickname = request.Nickname.Trim(),
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.Profiles.Add(profile);
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/players/{profile.UserId}", profile);
});

app.MapGet("/api/v1/teams", async (PlayersDbContext db) =>
{
    var teams = await db.Teams.OrderBy(team => team.Name).ToListAsync();
    var teamIds = teams.Select(team => team.Id).ToArray();
    var members = await db.TeamMembers.Where(member => teamIds.Contains(member.TeamId)).ToListAsync();
    return Results.Ok(teams.Select(team => new TeamResponse(
        team.Id,
        team.Name,
        team.OwnerId,
        members.Where(member => member.TeamId == team.Id).Select(member => member.UserId).ToArray())));
});

app.MapGet("/api/v1/teams/{teamId:guid}", async (Guid teamId, PlayersDbContext db) =>
{
    var team = await db.Teams.FindAsync(teamId);
    if (team is null)
        return Results.NotFound();

    var memberIds = await db.TeamMembers
        .Where(member => member.TeamId == teamId)
        .Select(member => member.UserId)
        .ToArrayAsync();
    return Results.Ok(new TeamResponse(team.Id, team.Name, team.OwnerId, memberIds));
});

app.MapPost("/api/v1/teams", async (
    CreateTeamRequest request,
    HttpContext context,
    PlayersDbContext db) =>
{
    var ownerId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    if (ownerId is null)
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80)
        return Results.BadRequest(new { error = "Название команды должно содержать от 1 до 80 символов." });
    if (!await db.Profiles.AnyAsync(profile => profile.UserId == ownerId))
        return Results.NotFound(new { error = "Профиль игрока не найден." });

    var team = new PlayerTeam
    {
        Id = Guid.NewGuid(),
        Name = request.Name.Trim(),
        OwnerId = ownerId,
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.Teams.Add(team);
    db.TeamMembers.Add(new TeamMember
    {
        TeamId = team.Id,
        UserId = ownerId,
        JoinedAt = team.CreatedAt
    });
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/teams/{team.Id}", new TeamResponse(
        team.Id, team.Name, team.OwnerId, [ownerId]));
}).RequireAuthorization();

app.MapPost("/api/v1/teams/{teamId:guid}/members", async (
    Guid teamId,
    HttpContext context,
    PlayersDbContext db) =>
{
    var userId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    if (userId is null)
        return Results.Unauthorized();
    var team = await db.Teams.FindAsync(teamId);
    if (team is null)
        return Results.NotFound();
    if (!await db.Profiles.AnyAsync(profile => profile.UserId == userId))
        return Results.NotFound(new { error = "Профиль игрока не найден." });
    if (await db.TeamMembers.AnyAsync(member => member.TeamId == teamId && member.UserId == userId))
        return Results.Conflict(new { error = "Игрок уже состоит в команде." });

    db.TeamMembers.Add(new TeamMember
    {
        TeamId = teamId,
        UserId = userId,
        JoinedAt = DateTimeOffset.UtcNow
    });
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/v1/teams/{teamId:guid}/members/me", async (
    Guid teamId,
    HttpContext context,
    PlayersDbContext db) =>
{
    var userId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    if (userId is null)
        return Results.Unauthorized();
    var team = await db.Teams.FindAsync(teamId);
    if (team is null)
        return Results.NotFound();
    if (team.OwnerId == userId)
        return Results.Conflict(new { error = "Владелец не может покинуть команду. Удалите команду." });

    var membership = await db.TeamMembers.FindAsync(teamId, userId);
    if (membership is null)
        return Results.NotFound();
    db.TeamMembers.Remove(membership);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/v1/teams/{teamId:guid}/members/{playerId}", async (
    Guid teamId,
    string playerId,
    HttpContext context,
    PlayersDbContext db) =>
{
    var ownerId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    if (ownerId is null)
        return Results.Unauthorized();
    var team = await db.Teams.FindAsync(teamId);
    if (team is null)
        return Results.NotFound();
    if (team.OwnerId != ownerId)
        return Results.Forbid();
    if (playerId == ownerId)
        return Results.Conflict(new { error = "Владелец не может исключить себя. Удалите команду." });

    var membership = await db.TeamMembers.FindAsync(teamId, playerId);
    if (membership is null)
        return Results.NotFound();
    db.TeamMembers.Remove(membership);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/v1/teams/{teamId:guid}", async (
    Guid teamId,
    HttpContext context,
    PlayersDbContext db) =>
{
    var ownerId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    if (ownerId is null)
        return Results.Unauthorized();
    var team = await db.Teams.FindAsync(teamId);
    if (team is null)
        return Results.NotFound();
    if (team.OwnerId != ownerId)
        return Results.Forbid();

    db.Teams.Remove(team);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.Run();

public sealed record CreatePlayerProfileRequest(string UserId, string Nickname);
public sealed record CreateTeamRequest(string Name);
public sealed record TeamResponse(Guid Id, string Name, string OwnerId, string[] Members);
public partial class Program;
