using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Finance.Api.Auth;

internal sealed class SessionTokens
{
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    internal const string CookieName = "__Host-finance-session";

    private const string Issuer = "personal-finance-api";
    private const string Audience = "personal-finance-spa";
    private const string SessionClaim = "sid";

    private readonly TimeProvider clock;
    private readonly SymmetricSecurityKey signingKey;

    public SessionTokens(IConfiguration configuration, TimeProvider clock)
    {
        this.clock = clock;
        var secret = configuration["Auth:SigningKey"]
            ?? throw new InvalidOperationException("Set Auth:SigningKey to a base64-encoded random key.");
        var bytes = Convert.FromBase64String(secret);
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("Auth:SigningKey needs at least 32 random bytes.");
        }

        signingKey = new SymmetricSecurityKey(bytes);
    }

    public void Configure(JwtBearerOptions options)
    {
        options.IncludeErrorDetails = false;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = signingKey,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                return expires > now && (notBefore is null || notBefore <= now);
            }
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.TryGetValue(CookieName, out var token))
                {
                    context.Token = token;
                }
                else
                {
                    context.NoResult();
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                if (context.Principal is null || ReadIdentity(context.Principal) is not { } identity)
                {
                    context.Fail("Invalid session claims.");
                    return;
                }

                var db = context.HttpContext.RequestServices.GetRequiredService<AuthDbContext>();
                var now = clock.GetUtcNow();
                if (!await ActiveSession(db, identity, now).AnyAsync(context.HttpContext.RequestAborted))
                {
                    context.Fail("Session expired or revoked.");
                }
            }
        };
    }

    public void WriteCookie(HttpResponse response, SessionIdentity identity, DateTimeOffset expires)
    {
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, identity.UserId),
                new Claim(SessionClaim, identity.SessionId.ToString())
            ],
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

        var cookie = CookieOptions();
        cookie.Expires = expires;
        response.Cookies.Append(CookieName, new JwtSecurityTokenHandler().WriteToken(token), cookie);
    }

    public static void ClearCookie(HttpResponse response) =>
        response.Cookies.Delete(CookieName, CookieOptions());

    public static SessionIdentity? ReadIdentity(ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return !string.IsNullOrWhiteSpace(userId) &&
            Guid.TryParse(principal.FindFirstValue(SessionClaim), out var sessionId)
            ? new SessionIdentity(userId, sessionId)
            : null;
    }

    public static IQueryable<AuthSession> ActiveSession(
        AuthDbContext db, SessionIdentity identity, DateTimeOffset now) =>
        db.Sessions.Where(session => session.Id == identity.SessionId &&
            session.UserId == identity.UserId && session.IdleExpiresAt > now);

    private static CookieOptions CookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true
    };
}

internal readonly record struct SessionIdentity(string UserId, Guid SessionId);
