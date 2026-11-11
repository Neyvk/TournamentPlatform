using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TournamentPlatform.Tests;

public class PlayersServiceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public PlayersServiceTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetPlayers_ReturnsOkWithEmptyList()
    {
        var response = await _client.GetAsync("/api/v1/players");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<object[]>() ?? []);
    }
}
