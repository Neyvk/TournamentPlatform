var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { service = "matches", status = "healthy" }));
app.MapGet("/api/v1/matches/{matchId:guid}", (Guid matchId) => Results.NotFound());
app.Run();
