using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using KanbanContracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KanbanApi.Infrastructure;

public sealed record JwtSettings(string Key, string Issuer, string Audience)
{
    public static JwtSettings Load(IConfiguration configuration, bool development)
    {
        var key = configuration["Jwt:Key"] ?? configuration["JWT_KEY"];
        if (string.IsNullOrWhiteSpace(key) && development) key = DatabaseConfiguration.ReadLocalValue("JWT_KEY");
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32 || key.StartsWith("replace-with-", StringComparison.Ordinal))
            throw new InvalidOperationException("Configure a random JWT_KEY of at least 32 bytes.");
        return new JwtSettings(key, configuration["Jwt:Issuer"] ?? "KanbanBoard.Api",
            configuration["Jwt:Audience"] ?? "KanbanBoard.Desktop");
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)),
        ValidateIssuer = true, ValidIssuer = Issuer, ValidateAudience = true, ValidAudience = Audience,
        ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
        ClockSkew = TimeSpan.Zero, ValidAlgorithms = [SecurityAlgorithms.HmacSha256], NameClaimType = "sub",
    };
}

public sealed class JwtService(JwtSettings settings, TimeProvider clock)
{
    public AuthResponse Issue(UserDto user, int sessionVersion = 0)
    {
        // Whole seconds keep response expiry and JWT exp exactly aligned.
        var now = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds()).UtcDateTime;
        var expiry = now.AddHours(24);
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience,
            [new Claim("sv", sessionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)), new Claim("sub", user.Id.ToString()), new Claim("jti", Guid.NewGuid().ToString("N")),
             new Claim("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)],
            now, expiry, new SigningCredentials(settings.ValidationParameters().IssuerSigningKey, SecurityAlgorithms.HmacSha256));
        return new AuthResponse(new JwtSecurityTokenHandler().WriteToken(token), expiry, user);
    }

    public static void Configure(JwtBearerOptions options, JwtSettings settings)
    {
        options.MapInboundClaims = false;
        options.IncludeErrorDetails = false;
        options.TokenValidationParameters = settings.ValidationParameters();
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                if (!Guid.TryParse(context.Principal?.FindFirstValue("sub"), out var id) || id == Guid.Empty)
                {
                    context.Fail("Invalid subject."); return;
                }
                if (!int.TryParse(context.Principal?.FindFirstValue("sv"), out var version) ||
                    !await context.HttpContext.RequestServices.GetRequiredService<KanbanDbContext>().Users.AsNoTracking()
                        .AnyAsync(u => u.Id == id && u.SessionVersion == version, context.HttpContext.RequestAborted))
                    context.Fail("Session revoked.");
            },
        };
    }
}
