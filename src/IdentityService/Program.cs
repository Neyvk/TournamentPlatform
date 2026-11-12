using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Identity")
    ?? throw new InvalidOperationException("ConnectionStrings:Identity is required.");
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is required.");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Jwt:Key must contain at least 32 bytes.");

builder.Services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddIdentityCore<IdentityUser>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = true;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireNonAlphanumeric = false;
})
.AddEntityFrameworkStores<AuthDbContext>();

var issuer = builder.Configuration["Jwt:Issuer"] ?? "TournamentPlatform.Identity";
var audience = builder.Configuration["Jwt:Audience"] ?? "TournamentPlatform";
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = issuer,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = signingKey,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    });
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { service = "identity", status = "healthy" }));

app.MapPost("/api/v1/auth/register", async (
    RegisterRequest request,
    UserManager<IdentityUser> users) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["credentials"] = ["Email и пароль обязательны."]
        });

    if (string.IsNullOrWhiteSpace(request.Nickname))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["nickname"] = ["Никнейм обязателен."]
        });

    var email = request.Email.Trim();
    var nickname = request.Nickname.Trim();
    var user = new IdentityUser { UserName = nickname, Email = email };
    var result = await users.CreateAsync(user, request.Password);
    if (!result.Succeeded)
        return Results.ValidationProblem(result.Errors
            .GroupBy(error => error.Code)
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));

    return Results.Created($"/api/v1/auth/users/{user.Id}", new
    {
        userId = user.Id,
        nickname = user.UserName,
        email = user.Email
    });
});

app.MapPost("/api/v1/auth/login", async (
    LoginRequest request,
    UserManager<IdentityUser> users) =>
{
    var login = request.Login.Trim();
    var user = login.Contains('@')
        ? await users.FindByEmailAsync(login)
        : await users.FindByNameAsync(login);
    if (user is null || !await users.CheckPasswordAsync(user, request.Password))
        return Results.Unauthorized();

    var now = DateTime.UtcNow;
    var expires = now.AddMinutes(app.Configuration.GetValue("Jwt:LifetimeMinutes", 60));
    var token = new JwtSecurityToken(
        issuer: issuer,
        audience: audience,
        claims:
        [
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim("nickname", user.UserName!)
        ],
        notBefore: now,
        expires: expires,
        signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

    return Results.Ok(new
    {
        accessToken = new JwtSecurityTokenHandler().WriteToken(token),
        tokenType = "Bearer",
        expiresAt = expires
    });
});

app.Run();

public sealed record RegisterRequest(string Email, string Nickname, string Password);
public sealed record LoginRequest(string Login, string Password);
public partial class Program;
