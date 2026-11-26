using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Finance.Api.Auth;

internal static class AuthEndpoints
{
    private static readonly EmailAddressAttribute EmailValidator = new();

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Ok(new { requestToken = antiforgery.GetAndStoreTokens(context).RequestToken }));
        auth.MapGet("/me", CurrentAccount).RequireAuthorization();

        var commands = auth.MapGroup("").AddEndpointFilter(ValidateAntiforgery);
        commands.MapPost("/signup", Signup);
        commands.MapPost("/login", Login);
        commands.MapPost("/logout", Logout).RequireAuthorization();
    }

    private static async ValueTask<object?> ValidateAntiforgery(
        EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Problem("The form expired. Submit it again.", statusCode: 400);
        }

        return await next(invocation);
    }

    private static async Task<IResult> Signup(Credentials input, UserManager<IdentityUser> users)
    {
        var email = input.Email?.Trim();
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrEmpty(email) || email.Length > 256 || !EmailValidator.IsValid(email))
        {
            errors["email"] = ["Enter a valid email address."];
        }
        if (string.IsNullOrWhiteSpace(input.Password) || input.Password.Length > 128)
        {
            errors["password"] = ["Enter a password of up to 128 characters."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var user = new IdentityUser { UserName = email, Email = email };
        IdentityResult result;
        try
        {
            result = await users.CreateAsync(user, input.Password!);
        }
        catch (DbUpdateException error) when (
            error.InnerException is PostgresException postgres &&
            postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
            postgres.ConstraintName is "EmailIndex" or "UserNameIndex")
        {
            return DuplicateEmail();
        }

        if (result.Succeeded)
        {
            return Results.StatusCode(StatusCodes.Status201Created);
        }
        if (result.Errors.Any(error => error.Code is "DuplicateEmail" or "DuplicateUserName"))
        {
            return DuplicateEmail();
        }

        return Results.ValidationProblem(result.Errors
            .GroupBy(error => error.Code.StartsWith("Password", StringComparison.Ordinal) ? "password" : "email")
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));
    }

    private static IResult DuplicateEmail() =>
        Results.Problem("An account already uses this email address.", statusCode: 409);

    private static async Task<IResult> Login(
        Credentials input, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn,
        AuthDbContext db, SessionTokens tokens, TimeProvider clock, HttpContext context,
        CancellationToken cancellationToken)
    {
        var email = input.Email?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > 256 ||
            string.IsNullOrEmpty(input.Password) || input.Password.Length > 128)
        {
            return Results.Challenge();
        }

        var user = await users.FindByEmailAsync(email);
        if (user is null ||
            !(await signIn.CheckPasswordSignInAsync(user, input.Password, lockoutOnFailure: true)).Succeeded)
        {
            return Results.Challenge();
        }

        var expires = clock.GetUtcNow() + SessionTokens.IdleTimeout;
        var identity = new SessionIdentity(user.Id, Guid.NewGuid());
        db.Sessions.Add(new AuthSession { Id = identity.SessionId, UserId = user.Id, IdleExpiresAt = expires });
        await db.SaveChangesAsync(cancellationToken);
        tokens.WriteCookie(context.Response, identity, expires);
        return Results.NoContent();
    }

    private static async Task<IResult> CurrentAccount(
        HttpContext context, UserManager<IdentityUser> users, AuthDbContext db,
        SessionTokens tokens, TimeProvider clock, CancellationToken cancellationToken)
    {
        var identity = SessionTokens.ReadIdentity(context.User)!.Value;
        var user = await users.FindByIdAsync(identity.UserId);
        if (user is null)
        {
            return Results.Challenge();
        }

        var now = clock.GetUtcNow();
        var expires = now + SessionTokens.IdleTimeout;
        var updated = await SessionTokens.ActiveSession(db, identity, now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.IdleExpiresAt,
                session => session.IdleExpiresAt > expires ? session.IdleExpiresAt : expires), cancellationToken);
        if (updated == 0)
        {
            return Results.Challenge();
        }

        tokens.WriteCookie(context.Response, identity, expires);
        return Results.Ok(new { user.Id, user.Email });
    }

    private static async Task<IResult> Logout(
        HttpContext context, AuthDbContext db, CancellationToken cancellationToken)
    {
        var identity = SessionTokens.ReadIdentity(context.User)!.Value;
        await db.Sessions.Where(session => session.Id == identity.SessionId && session.UserId == identity.UserId)
            .ExecuteDeleteAsync(cancellationToken);
        SessionTokens.ClearCookie(context.Response);
        return Results.NoContent();
    }

    private sealed record Credentials(string? Email, string? Password);
}
