var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { service = "tournaments", status = "healthy" }));
app.MapGet("/api/v1/tournaments", () => Results.Ok(Array.Empty<object>()));
app.Run();
