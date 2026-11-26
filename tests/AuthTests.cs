using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Finance.Api.Tests;

public class AuthTests
{
    private const string Password = "Finance!A1";
    private const string SessionCookie = "__Host-finance-session";

    [Fact]
    public async Task MissingCredentialsAndAntiforgeryTokenAreRejected()
    {
        using var app = new AuthApp();
        using var browser = app.CreateBrowser();

        using var unauthorized = await browser.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var missingToken = await browser.PostAsJsonAsync("/api/auth/signup",
            new { email = "test@example.com", password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);

        using var invalidEmail = await Post(browser, "signup", new { email = "invalid", password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, invalidEmail.StatusCode);
    }

    [Fact]
    public async Task LoopbackProxyPreservesHttpsForAntiforgeryCookie()
    {
        using var app = new AuthApp();
        var response = await app.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Method = "GET";
            context.Request.Scheme = "http";
            context.Request.Path = "/api/auth/csrf";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        Assert.Equal(200, response.Response.StatusCode);
        Assert.Contains("secure", response.Response.Headers.SetCookie.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpiredAndInvalidlySignedJwtAreRejectedBeforeDatabaseAccess()
    {
        using var app = new AuthApp();
        using var browser = app.CreateBrowser();

        var expired = IssueJwt(app.Key, app.Clock.GetUtcNow().AddMinutes(-1));
        using var expiredResponse = await GetWithToken(browser, expired);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredResponse.StatusCode);

        var invalid = IssueJwt(RandomNumberGenerator.GetBytes(32), app.Clock.GetUtcNow().AddMinutes(30));
        using var invalidResponse = await GetWithToken(browser, invalid);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
    }

    [Fact]
    [Trait("Dependency", "PostgreSQL")]
    public async Task SignupAndSigninPersistAcrossAppRestart()
    {
        var connection = LiveConnection();
        var email = NewEmail();
        var key = RandomNumberGenerator.GetBytes(32);
        string jwt;
        using (var app = new AuthApp(connection, key))
        using (var browser = app.CreateBrowser())
        {
            using var signup = await Post(browser, "signup", new { email, password = Password });
            Assert.Equal(HttpStatusCode.Created, signup.StatusCode);
            using var duplicate = await Post(browser, "signup",
                new { email = email.ToUpperInvariant(), password = Password });
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

            using var wrongPassword = await Post(browser, "login", new { email, password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
            using var login = await Post(browser, "login", new { email, password = Password });
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
            jwt = ReadSessionToken(login);
        }

        using var restarted = new AuthApp(connection, key);
        using var newBrowser = restarted.CreateBrowser();
        using var account = await GetWithToken(newBrowser, jwt);
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        var original = await account.Content.ReadFromJsonAsync<Account>();
        Assert.Equal(email, original!.Email);

        var otherEmail = NewEmail();
        using var otherBrowser = restarted.CreateBrowser();
        await CreateAndSignIn(otherBrowser, otherEmail);
        var other = await otherBrowser.GetFromJsonAsync<Account>("/api/auth/me");
        Assert.Equal(otherEmail, other!.Email);
        Assert.NotEqual(original.Id, other.Id);
    }

    [Fact]
    [Trait("Dependency", "PostgreSQL")]
    public async Task ActivityExtendsExpiryAndSignoutRevokesOnlyTheCurrentSession()
    {
        using var app = new AuthApp(LiveConnection());
        using var first = app.CreateBrowser();
        using var second = app.CreateBrowser();
        var email = NewEmail();
        var oldJwt = await CreateAndSignIn(first, email);
        using var secondLogin = await Post(second, "login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, secondLogin.StatusCode);

        app.Clock.Advance(TimeSpan.FromMinutes(20));
        using var firstTouch = await first.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, firstTouch.StatusCode);
        var renewedJwt = ReadSessionToken(firstTouch);
        Assert.NotEqual(oldJwt, renewedJwt);

        using var secondTouch = await second.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, secondTouch.StatusCode);

        app.Clock.Advance(TimeSpan.FromMinutes(20));
        using var beforeSignout = await first.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, beforeSignout.StatusCode);
        var jwtBeforeSignout = ReadSessionToken(beforeSignout);
        using var signout = await Post(first, "logout");
        Assert.Equal(HttpStatusCode.NoContent, signout.StatusCode);
        using var replay = await GetWithToken(first, jwtBeforeSignout);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        using var stillSignedIn = await second.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
        app.Clock.Advance(TimeSpan.FromMinutes(30));
        using var expired = await second.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    private static async Task<string> CreateAndSignIn(HttpClient browser, string email)
    {
        using var signup = await Post(browser, "signup", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Created, signup.StatusCode);
        using var login = await Post(browser, "login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return ReadSessionToken(login);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient browser, string action, object? body = null)
    {
        var csrf = await browser.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/auth/{action}");
        request.Headers.Add("X-CSRF-Token", csrf!.RequestToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await browser.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetWithToken(HttpClient browser, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", $"{SessionCookie}={token}");
        return await browser.SendAsync(request);
    }

    private static string ReadSessionToken(HttpResponseMessage response)
    {
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{SessionCookie}=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        return cookie[(SessionCookie.Length + 1)..cookie.IndexOf(';')];
    }

    private static string IssueJwt(byte[] key, DateTimeOffset expires) => new JwtSecurityTokenHandler().WriteToken(
        new JwtSecurityToken("personal-finance-api", "personal-finance-spa",
            [new Claim("sub", "test"), new Claim("sid", Guid.NewGuid().ToString())],
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)));

    private static string LiveConnection() => Environment.GetEnvironmentVariable("FINANCE_AUTH_TEST_CONNECTION")
        ?? throw new InvalidOperationException("Set FINANCE_AUTH_TEST_CONNECTION to a migrated test database.");

    private static string NewEmail() => $"{Guid.NewGuid():N}@example.com";

    private sealed record Account(string Id, string Email);
    private sealed record CsrfResponse(string RequestToken);

    private sealed class AuthApp(string? connection = null, byte[]? key = null) : WebApplicationFactory<Program>
    {
        public byte[] Key { get; } = key ?? RandomNumberGenerator.GetBytes(32);
        public TestClock Clock { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Auth:SigningKey"] = Convert.ToBase64String(Key),
                    ["ConnectionStrings:Finance"] = connection
                        ?? "Host=127.0.0.1;Port=1;Database=unused;Timeout=1;"
                }));
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock));
        }

        public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow.AddDays(1);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
