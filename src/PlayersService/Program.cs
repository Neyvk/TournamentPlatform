var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { service = "players", status = "healthy" }));
app.MapGet("/api/v1/players", () => Results.Ok(Array.Empty<object>()));
app.MapGet("/api/v1/teams", () => Results.Ok(Array.Empty<object>()));
app.Run();

public partial class Program;
