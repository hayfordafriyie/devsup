using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DevSup.Agent.Git;
using DevSup.Agent.Repair;
using DevSup.Api;
using DevSup.Api.Auth;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Core.Services;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Email;
using DevSup.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DevSup")
    ?? "Data Source=devsup.db";

builder.Services.AddDbContext<DevSupDbContext>(options =>
    options.UseSqlite(connectionString));

var jwtSettings = new JwtSettings
{
    Issuer = builder.Configuration["Jwt:Issuer"] ?? "devsup",
    Audience = builder.Configuration["Jwt:Audience"] ?? "devsup-clients",
    SecretKey = builder.Configuration["Jwt:SecretKey"]
        ?? throw new InvalidOperationException("Jwt:SecretKey is required."),
    ExpiryMinutes = int.TryParse(builder.Configuration["Jwt:ExpiryMinutes"], out var minutes) ? minutes : 60
};

builder.Services.AddSingleton(jwtSettings);
builder.Services.AddSingleton<JwtTokenIssuer>();
builder.Services.AddSingleton<IPasswordHasherService, PasswordHasherService>();
builder.Services.AddSingleton<IFailureClassifier, FailureClassifier>();

var smtp = builder.Configuration.GetSection("Smtp").Get<SmtpSettings>() ?? new SmtpSettings();
builder.Services.AddSingleton(smtp);
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

var emailOptions = new EmailWorkerOptions
{
    IntervalSeconds = int.TryParse(builder.Configuration["Emailing:IntervalSeconds"], out var interval) ? interval : 15,
    BatchSize = int.TryParse(builder.Configuration["Emailing:BatchSize"], out var batch) ? batch : 25,
    MaxAttempts = int.TryParse(builder.Configuration["Emailing:MaxAttempts"], out var attempts) ? attempts : 5
};
builder.Services.AddSingleton(emailOptions);
builder.Services.AddScoped<EmailOutboxProcessor>(sp => new EmailOutboxProcessor(
    sp.GetRequiredService<DevSupDbContext>(),
    sp.GetRequiredService<IEmailSender>(),
    sp.GetRequiredService<ILogger<EmailOutboxProcessor>>(),
    emailOptions.MaxAttempts));
builder.Services.AddHostedService<EmailOutboxWorker>();

var dataProtectionKey = builder.Configuration["Security:DataProtectionKey"]
    ?? "devsup-dev-only-data-protection-key-change-in-production";
var keyProtector = new AesGcmKeyProtector(dataProtectionKey);
builder.Services.AddSingleton<IKeyProtector>(keyProtector);

var github = builder.Configuration.GetSection("GitHub").Get<GitHubAuthSettings>() ?? new GitHubAuthSettings();
builder.Services.AddSingleton(github);
builder.Services.AddHttpClient<IGitHubGateway, GitHubGateway>(client => client.Timeout = TimeSpan.FromSeconds(15));

var repairOptions = new RepairWorkerOptions
{
    IntervalSeconds = int.TryParse(builder.Configuration["Repairing:IntervalSeconds"], out var repairInterval) ? repairInterval : 20,
    BatchSize = int.TryParse(builder.Configuration["Repairing:BatchSize"], out var repairBatch) ? repairBatch : 5,
    GitUserName = builder.Configuration["Repairing:GitUserName"] ?? "DevSup Bot",
    GitUserEmail = builder.Configuration["Repairing:GitUserEmail"] ?? "devsup@localhost"
};
builder.Services.AddSingleton(repairOptions);
builder.Services.AddSingleton<IGitAdapter, GitCliAdapter>();
builder.Services.AddSingleton<IRepairProvider, HeuristicRepairProvider>();
builder.Services.AddScoped<RepairProcessor>();
builder.Services.AddHostedService<RepairWorker>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
    if (db.Database.IsRelational())
    {
        await db.Database.MigrateAsync();
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { service = "DevSup", status = "ok" }));

app.MapPost("/api/users/register", async (RegisterUserRequest request, DevSupDbContext db, IPasswordHasherService hasher, CancellationToken ct) =>
{
    if (!IsValidEmail(request.Email))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "A valid email address is required.");
    }

    if (string.IsNullOrWhiteSpace(request.DisplayName))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Display name is required.");
    }

    if (request.Password is null || request.Password.Length < 8)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Password must be at least 8 characters.");
    }

    var normalizedEmail = request.Email.Trim().ToLowerInvariant();
    if (await db.Users.AnyAsync(u => u.Email == normalizedEmail, ct))
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "An account with this email already exists.");
    }

    var user = new User
    {
        Id = Guid.NewGuid(),
        Email = normalizedEmail,
        DisplayName = request.DisplayName.Trim(),
        PasswordHash = hasher.Hash(request.Password),
        CreatedAt = DateTimeOffset.UtcNow
    };

    db.Users.Add(user);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/users/{user.Id}", new UserResponse(user.Id, user.Email, user.DisplayName));
});

app.MapPost("/api/users/login", async (LoginRequest request, DevSupDbContext db, IPasswordHasherService hasher, JwtTokenIssuer issuer, CancellationToken ct) =>
{
    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == request.Email.Trim().ToLowerInvariant(), ct);
    if (user is null || !hasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
    {
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, detail: "Invalid email or password.");
    }

    var (token, expiresAt) = issuer.Issue(user);
    return Results.Ok(new LoginResponse(token, expiresAt, new UserResponse(user.Id, user.Email, user.DisplayName)));
});

const string OAuthStateCookie = "devsup_oauth_state";

app.MapGet("/api/auth/github/login", (HttpContext http, IGitHubGateway gateway, GitHubAuthSettings github) =>
{
    if (!github.IsConfigured)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            detail: "GitHub OAuth is not configured. Set GitHub:ClientId and GitHub:ClientSecret.");
    }

    var state = Guid.NewGuid().ToString("N");
    http.Response.Cookies.Append(OAuthStateCookie, state, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromMinutes(10)
    });

    return Results.Redirect(gateway.BuildAuthorizeUrl(state));
});

app.MapGet("/api/auth/github/callback", async (
    string code,
    string? state,
    HttpContext http,
    IGitHubGateway gateway,
    GitHubAuthSettings github,
    DevSupDbContext db,
    IPasswordHasherService hasher,
    JwtTokenIssuer issuer,
    IKeyProtector protector,
    CancellationToken ct) =>
{
    if (!github.IsConfigured)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            detail: "GitHub OAuth is not configured.");
    }

    var expectedState = http.Request.Cookies[OAuthStateCookie];
    if (string.IsNullOrWhiteSpace(state)
        || expectedState is null
        || !string.Equals(state, expectedState, StringComparison.Ordinal))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
            detail: "OAuth state mismatch — start the login flow again.");
    }

    http.Response.Cookies.Delete(OAuthStateCookie);
    if (string.IsNullOrWhiteSpace(code))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Missing authorization code.");
    }

    GitHubTokenResult tokenResult;
    GitHubProfile profile;
    try
    {
        tokenResult = await gateway.ExchangeCodeAsync(code, ct);
        profile = await gateway.GetProfileAsync(tokenResult.AccessToken, ct);
    }
    catch (Exception ex)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
            detail: $"GitHub OAuth exchange failed: {ex.Message}");
    }

    if (string.IsNullOrWhiteSpace(profile.Email))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
            detail: "DevSup could not read an email from your GitHub profile. Confirm a public email on GitHub, then retry.");
    }

    var email = profile.Email.Trim().ToLowerInvariant();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

    if (user is null)
    {
        user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = profile.Name ?? profile.Login ?? email,
            PasswordHash = hasher.Hash("github-oauth-" + Guid.NewGuid().ToString("N")),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
    }

    var encrypted = protector.Protect(tokenResult.AccessToken);
    var existingToken = await db.OAuthTokens.FirstOrDefaultAsync(
        o => o.UserId == user.Id && o.Provider == GitProvider.GitHub, ct);

    if (existingToken is null)
    {
        db.OAuthTokens.Add(new OAuthToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Provider = GitProvider.GitHub,
            EncryptedAccessToken = encrypted,
            Scope = tokenResult.Scope,
            LinkedAt = DateTimeOffset.UtcNow
        });
    }
    else
    {
        var tokenEntry = db.Entry(existingToken);
        tokenEntry.Property(t => t.EncryptedAccessToken).CurrentValue = encrypted;
        tokenEntry.Property(t => t.Scope).CurrentValue = tokenResult.Scope;
        tokenEntry.Property(t => t.LinkedAt).CurrentValue = DateTimeOffset.UtcNow;
    }

    await db.SaveChangesAsync(ct);

    var (jwt, expiresAt) = issuer.Issue(user);
    return Results.Ok(new LoginResponse(jwt, expiresAt, new UserResponse(user.Id, user.Email, user.DisplayName)));
});

app.MapPost("/api/repositories", async (CreateRepositoryRequest request, ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();

    if (string.IsNullOrWhiteSpace(request.CloneUrl) || !IsAbsoluteHttpUrl(request.CloneUrl))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Clone URL must be an absolute http(s) URL.");
    }

    if (string.IsNullOrWhiteSpace(request.DefaultBranch))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "A default branch is required.");
    }

    var repository = new ConnectedRepository
    {
        Id = Guid.NewGuid(),
        OwnerUserId = ownerId,
        Provider = request.Provider,
        CloneUrl = request.CloneUrl,
        DefaultBranch = request.DefaultBranch,
        AppUrl = request.AppUrl,
        ConnectedAt = DateTimeOffset.UtcNow
    };

    db.ConnectedRepositories.Add(repository);

    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "This repository is already connected.");
    }

    return Results.Created($"/api/repositories/{repository.Id}",
        new RepositoryResponse(repository.Id, repository.Provider.ToString(), repository.CloneUrl, repository.DefaultBranch, repository.AppUrl));
}).RequireAuthorization();

app.MapGet("/api/repositories", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var repositories = await db.ConnectedRepositories
        .AsNoTracking()
        .Where(r => r.OwnerUserId == ownerId)
        .Select(r => new RepositoryResponse(r.Id, r.Provider.ToString(), r.CloneUrl, r.DefaultBranch, r.AppUrl))
        .ToListAsync(ct);

    return Results.Ok(repositories);
}).RequireAuthorization();

app.MapPost("/api/ingest", async (IngestFailureRequest request, ClaimsPrincipal user, DevSupDbContext db, IFailureClassifier classifier, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();

    var repository = await db.ConnectedRepositories.AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == request.RepositoryId && r.OwnerUserId == ownerId, ct);

    if (repository is null)
    {
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Repository not found for this account.");
    }

    var failure = new FailureEvent
    {
        Id = Guid.NewGuid(),
        RepositoryId = repository.Id,
        StatusCode = request.StatusCode,
        Method = request.Method,
        Path = request.Path,
        RequestPayload = Truncate(request.RequestPayload, 8192),
        ResponsePayload = Truncate(request.ResponsePayload, 8192),
        ExceptionMessage = Truncate(request.ExceptionMessage, 4096),
        StackTrace = Truncate(request.StackTrace, 16_384),
        OccurredAt = DateTimeOffset.UtcNow
    };

    db.FailureEvents.Add(failure);

    var (category, kind) = classifier.Classify(failure);
    var status = category == FailureCategory.NotCodeError ? TicketStatus.SkippedNotCodeError : TicketStatus.New;

    var ticket = new RepairTicket
    {
        Id = Guid.NewGuid(),
        FailureEventId = failure.Id,
        RepositoryId = repository.Id,
        Category = category,
        Kind = kind,
        Status = status,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    db.RepairTickets.Add(ticket);

    var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ownerId, ct);
    if (owner is not null)
    {
        var notCodeError = category == FailureCategory.NotCodeError;
        db.EmailMessages.Add(new EmailMessage
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            To = owner.Email,
            Subject = notCodeError
                ? $"DevSup: not a code error on {failure.Method} {failure.Path}"
                : $"DevSup: failure detected on {failure.Method} {failure.Path}",
            HtmlBody = notCodeError
                ? $"<p>DevSup detected a <strong>{kind}</strong> failure on <code>{failure.Method} {failure.Path}</code> " +
                  $"(HTTP {failure.StatusCode}).</p>" +
                  $"<p>This was classified as <em>not a code error</em>, so the repair agent will <strong>not</strong> " +
                  $"attempt a code fix and no patch is scheduled. Review the credentials, client, rate limits, or " +
                  $"downstream services instead.</p>"
                : $"<p>DevSup detected a failure on <code>{failure.Method} {failure.Path}</code> " +
                  $"with status <strong>{failure.StatusCode}</strong>.</p>" +
                  $"<p>Classification: <strong>{kind}</strong> ({category}).</p>" +
                  $"<p>Next step: ticket <strong>{ticket.Status}</strong> — the agent will investigate code errors.</p>",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/tickets/{ticket.Id}",
        new IngestResponse(failure.Id, ticket.Id, category.ToString(), kind.ToString(), status.ToString()));
}).RequireAuthorization();

app.MapGet("/api/tickets", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();

    var tickets = (await db.RepairTickets
        .AsNoTracking()
        .Where(t => db.ConnectedRepositories.Any(r => r.Id == t.RepositoryId && r.OwnerUserId == ownerId))
        .ToListAsync(ct))
        .OrderByDescending(t => t.UpdatedAt)
        .Select(t => new TicketResponse(
            t.Id,
            t.FailureEventId,
            t.RepositoryId,
            t.Category.ToString(),
            t.Kind.ToString(),
            t.Status.ToString(),
            t.Analysis,
            t.PatchSummary,
            t.CommitSha,
            t.UpdatedAt))
        .ToList();

    return Results.Ok(tickets);
}).RequireAuthorization();

app.Run();

static bool IsValidEmail(string? email)
{
    if (string.IsNullOrWhiteSpace(email))
    {
        return false;
    }

    var parts = email.Trim().Split('@', StringSplitOptions.RemoveEmptyEntries);
    return parts.Length == 2 && parts[0].Length > 0 && parts[1].Contains('.', StringComparison.Ordinal);
}

static bool IsAbsoluteHttpUrl(string value)
    => Uri.TryCreate(value, UriKind.Absolute, out var uri)
       && uri.Scheme is "http" or "https"
       && uri.Host.Length > 0;

static string? Truncate(string? value, int maxLength)
    => value is null || value.Length <= maxLength ? value : value[..maxLength];

public partial class Program;