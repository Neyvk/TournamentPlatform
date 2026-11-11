var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { service = "rating", status = "healthy" }));
app.MapGet("/api/v1/ratings/players/{playerId:guid}", (Guid playerId) => Results.NotFound());
app.MapGet("/api/v1/ratings/teams/{teamId:guid}", (Guid teamId) => Results.NotFound());
app.MapGet("/api/v1/ratings/leaderboard", () => Results.Ok(Array.Empty<object>()));
app.Run();
