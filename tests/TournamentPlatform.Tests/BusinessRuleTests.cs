extern alias gateway;
extern alias identity;
extern alias players;
extern alias tournaments;
extern alias matches;
extern alias rating;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using GatewayProgram = gateway::Program;
using IdentityProgram = identity::Program;
using PlayersProgram = players::Program;
using TournamentsProgram = tournaments::Program;
using MatchesProgram = matches::Program;
using RatingProgram = rating::Program;

namespace TournamentPlatform.Tests;

public class BusinessRuleTests
{
    private const string JwtKey = "test-only-signing-key-with-more-than-thirty-two-bytes";
    private const string JwtIssuer = "TestIssuer";
    private const string JwtAudience = "TestAudience";

    static BusinessRuleTests()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Identity", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__Players", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__Tournaments", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__Matches", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__Rating", ConnectionString);
        Environment.SetEnvironmentVariable("Jwt__Key", JwtKey);
        Environment.SetEnvironmentVariable("Jwt__Issuer", JwtIssuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", JwtAudience);
        Environment.SetEnvironmentVariable("RabbitMQ__Host", "127.0.0.1");
        Environment.SetEnvironmentVariable("RabbitMQ__Port", "1");
    }

    private const string ConnectionString = "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public async Task Identity_RejectsRegistrationWithoutRequiredFields()
    {
        using var factory = CreateFactory<IdentityProgram>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "",
            nickname = "TeamOwner",
            password = "Password123"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Identity_RejectsRegistrationWithoutNickname()
    {
        using var factory = CreateFactory<IdentityProgram>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "person@example.com",
            nickname = " ",
            password = "Password123"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Identity_RejectsNicknameLongerThanAllowedLength()
    {
        using var factory = CreateFactory<IdentityProgram>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "person@example.com",
            nickname = new string('n', 81),
            password = "Password123"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Players_RejectsTeamNameLongerThanAllowedLength()
    {
        using var factory = CreateFactory<PlayersProgram>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("owner-1"));

        var response = await client.PostAsJsonAsync("/api/v1/teams", new { name = new string('x', 81) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Players_RejectsBlankTeamName()
    {
        using var factory = CreateFactory<PlayersProgram>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("owner-1"));

        var response = await client.PostAsJsonAsync("/api/v1/teams", new { name = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Players_RequiresAnAuthenticatedOwnerToCreateTeam()
    {
        using var factory = CreateFactory<PlayersProgram>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/teams", new { name = "Valid team" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("round_robin", 17)]
    [InlineData("olympic", 129)]
    [InlineData("swiss", 257)]
    public async Task Tournaments_RejectsTeamCountAboveFormatLimit(string format, int maxTeams)
    {
        using var factory = CreateFactory<TournamentsProgram>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("organizer-1"));

        var response = await client.PostAsJsonAsync("/api/v1/tournaments", new
        {
            name = "Test tournament",
            format,
            maxTeams,
            roundCount = format == "swiss" ? 3 : (int?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Tournaments_RequiresRoundCountForSwissFormat()
    {
        using var factory = CreateFactory<TournamentsProgram>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("organizer-1"));

        var response = await client.PostAsJsonAsync("/api/v1/tournaments", new
        {
            name = "Swiss tournament",
            format = "swiss",
            maxTeams = 8
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Tournaments_RejectsFewerThanTwoTeams()
    {
        using var factory = CreateFactory<TournamentsProgram>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("organizer-1"));

        var response = await client.PostAsJsonAsync("/api/v1/tournaments", new
        {
            name = "Cup",
            format = "olympic",
            maxTeams = 1
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Tournaments_RejectsRoundCountOnNonSwissFormat()
    {
        using var factory = CreateFactory<TournamentsProgram>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("organizer-1"));

        var response = await client.PostAsJsonAsync("/api/v1/tournaments", new
        {
            name = "Cup",
            format = "round_robin",
            maxTeams = 4,
            roundCount = 3
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Tournaments_RejectsUnknownFormat()
    {
        using var factory = CreateFactory<TournamentsProgram>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("organizer-1"));

        var response = await client.PostAsJsonAsync("/api/v1/tournaments", new
        {
            name = "Cup",
            format = "random",
            maxTeams = 4
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(-1, 0)]
    public async Task Matches_RejectsInvalidScore(int scoreA, int scoreB)
    {
        using var factory = CreateFactory<MatchesProgram>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("organizer-1"));

        var response = await client.PutAsJsonAsync(
            "/api/v1/matches/00000000-0000-0000-0000-000000000001/result",
            new { scoreA, scoreB });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Matches_RejectsNegativeScore()
    {
        using var factory = CreateFactory<MatchesProgram>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("organizer-1"));

        var response = await client.PutAsJsonAsync(
            "/api/v1/matches/00000000-0000-0000-0000-000000000001/result",
            new { scoreA = -1, scoreB = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Matches_RejectsTie()
    {
        using var factory = CreateFactory<MatchesProgram>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("organizer-1"));

        var response = await client.PutAsJsonAsync(
            "/api/v1/matches/00000000-0000-0000-0000-000000000001/result",
            new { scoreA = 2, scoreB = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rating_RejectsMatchWithoutTwoDistinctTeamsAndDecisiveScore()
    {
        using var factory = CreateFactory<RatingProgram>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/internal/ratings/matches", new
        {
            matchId = Guid.NewGuid(),
            teamAId = "same-team",
            teamBId = "same-team",
            scoreA = 2,
            scoreB = 1
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Rating_RejectsLeaderboardLimitOutsideAllowedRange(int limit)
    {
        using var factory = CreateFactory<RatingProgram>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/ratings/leaderboard?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rating_RejectsTieAsMatchResult()
    {
        using var factory = CreateFactory<RatingProgram>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/internal/ratings/matches", new
        {
            matchId = Guid.NewGuid(),
            teamAId = "team-a",
            teamBId = "team-b",
            scoreA = 1,
            scoreB = 1
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rating_RejectsEmptyTeamId()
    {
        using var factory = CreateFactory<RatingProgram>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/internal/ratings/matches", new
        {
            matchId = Guid.NewGuid(),
            teamAId = "",
            teamBId = "team-b",
            scoreA = 2,
            scoreB = 1
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string CreateToken(string userId)
    {
        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: [new Claim(JwtRegisteredClaimNames.Sub, userId)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static WebApplicationFactory<TProgram> CreateFactory<TProgram>() where TProgram : class =>
        new WebApplicationFactory<TProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = JwtKey,
                    ["Jwt:Issuer"] = JwtIssuer,
                    ["Jwt:Audience"] = JwtAudience,
                    ["RabbitMQ:Host"] = "127.0.0.1",
                    ["RabbitMQ:Port"] = "1",
                    ["PlayersService:BaseUrl"] = "http://localhost:8082/",
                    ["MatchesService:BaseUrl"] = "http://localhost:8084/",
                    ["TournamentsService:BaseUrl"] = "http://localhost:8083/",
                    ["TournamentsService:SelfBaseUrl"] = "http://localhost:8083/",
                    ["RatingService:SelfBaseUrl"] = "http://localhost:8085/"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(DbContextOptions<identity::AuthDbContext>));
                services.RemoveAll(typeof(DbContextOptions<players::PlayersDbContext>));
                services.RemoveAll(typeof(DbContextOptions<tournaments::TournamentsDbContext>));
                services.RemoveAll(typeof(DbContextOptions<matches::MatchesDbContext>));
                services.RemoveAll(typeof(DbContextOptions<rating::RatingDbContext>));
                services.AddDbContext<identity::AuthDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                services.AddDbContext<players::PlayersDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                services.AddDbContext<tournaments::TournamentsDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                services.AddDbContext<matches::MatchesDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                services.AddDbContext<rating::RatingDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            });
        });
}
