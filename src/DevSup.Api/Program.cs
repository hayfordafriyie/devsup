using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DevSup.Agent.Ai;
using DevSup.Agent.Git;
using DevSup.Agent.PullRequests;
using DevSup.Agent.Repair;
using DevSup.Api;
using DevSup.Api.Audit;
using DevSup.Api.Auth;
using DevSup.Api.Infra;
using DevSup.Api.Notifications;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Core.Services;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Digest;
using DevSup.Infrastructure.Email;
using DevSup.Infrastructure.HealthChecks;
using DevSup.Infrastructure.Retention;
using DevSup.Infrastructure.Security;
using DevSup.Infrastructure.Webhooks;
using DevSup.Infrastructure.Verification;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DevSup")
    ?? "Data Source=devsup.db";

var databaseProvider = builder.Configuration["Database:Provider"];
builder.Services.AddDbContext<DevSupDbContext>(options =>
    DatabaseOptions.UseProvider(options, databaseProvider, connectionString));

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

var digestOptions = new DigestOptions
{
    Enabled = !string.Equals(builder.Configuration["Digests:Enabled"], "false", StringComparison.OrdinalIgnoreCase),
    IntervalHours = int.TryParse(builder.Configuration["Digests:IntervalHours"], out var digestHours) && digestHours >= 1 ? digestHours : 24,
    MaxOpenTickets = int.TryParse(builder.Configuration["Digests:MaxOpenTickets"], out var digestMax) && digestMax >= 1 ? digestMax : 10
};
builder.Services.AddSingleton(digestOptions);
builder.Services.AddScoped<DigestProcessor>();
builder.Services.AddHostedService<DigestWorker>();

var webhookOptions = new WebhookWorkerOptions
{
    IntervalSeconds = int.TryParse(builder.Configuration["Webhooks:IntervalSeconds"], out var webhookInterval) ? webhookInterval : 15,
    BatchSize = int.TryParse(builder.Configuration["Webhooks:BatchSize"], out var webhookBatch) ? webhookBatch : 50,
    MaxAttempts = int.TryParse(builder.Configuration["Webhooks:MaxAttempts"], out var webhookAttempts) ? webhookAttempts : 8
};
builder.Services.AddSingleton(webhookOptions);
builder.Services.AddSingleton<IWebhookDeliverer, HttpWebhookDeliverer>();
builder.Services.AddScoped<WebhookOutboxProcessor>(sp => new WebhookOutboxProcessor(
    sp.GetRequiredService<DevSupDbContext>(),
    sp.GetRequiredService<IWebhookDeliverer>(),
    sp.GetRequiredService<IKeyProtector>(),
    sp.GetRequiredService<ILogger<WebhookOutboxProcessor>>(),
    webhookOptions.MaxAttempts));
builder.Services.AddHostedService<WebhookOutboxWorker>();

var healthOptions = new HealthCheckOptions
{
    Enabled = bool.TryParse(builder.Configuration["HealthChecks:Enabled"], out var healthEnabled) ? healthEnabled : true,
    IntervalSeconds = int.TryParse(builder.Configuration["HealthChecks:IntervalSeconds"], out var healthInterval) ? healthInterval : 300,
    TimeoutSeconds = int.TryParse(builder.Configuration["HealthChecks:TimeoutSeconds"], out var healthTimeout) ? healthTimeout : 10,
    BatchSize = int.TryParse(builder.Configuration["HealthChecks:BatchSize"], out var healthBatch) ? healthBatch : 20
};
builder.Services.AddSingleton(healthOptions);
builder.Services.AddSingleton<IAppUrlProber, HttpAppUrlProber>();
builder.Services.AddScoped<AppHealthChecker>();
    builder.Services.AddScoped<AuditRecorder>();
builder.Services.AddHostedService<AppHealthCheckWorker>();

var verificationOptions = new VerificationOptions
{
    Enabled = !string.Equals(builder.Configuration["Verification:Enabled"], "false", StringComparison.OrdinalIgnoreCase),
    IntervalSeconds = int.TryParse(builder.Configuration["Verification:IntervalSeconds"], out var verificationInterval) && verificationInterval >= 1 ? verificationInterval : 60,
    ProbeDelayMinutes = int.TryParse(builder.Configuration["Verification:ProbeDelayMinutes"], out var verificationDelay) && verificationDelay >= 1 ? verificationDelay : 2,
    WindowMinutes = int.TryParse(builder.Configuration["Verification:WindowMinutes"], out var verificationWindow) && verificationWindow >= 1 ? verificationWindow : 30,
    ProbeTimeoutSeconds = int.TryParse(builder.Configuration["Verification:ProbeTimeoutSeconds"], out var verificationTimeout) && verificationTimeout >= 1 ? verificationTimeout : 10
};
builder.Services.AddSingleton(verificationOptions);
builder.Services.AddScoped<VerificationProcessor>();
builder.Services.AddHostedService<VerificationWorker>();

var retentionOptions = new RetentionOptions
{
    WindowDays = int.TryParse(builder.Configuration["Retention:WindowDays"], out var retentionDays) ? retentionDays : 365,
    IntervalHours = int.TryParse(builder.Configuration["Retention:IntervalHours"], out var retentionHours) ? retentionHours : 24,
    BatchSize = int.TryParse(builder.Configuration["Retention:BatchSize"], out var retentionBatch) ? retentionBatch : 500
};
builder.Services.AddSingleton(retentionOptions);
builder.Services.AddScoped<RetentionCleaner>();
builder.Services.AddHostedService<RetentionWorker>();

var dataProtectionKey = builder.Configuration["Security:DataProtectionKey"]
    ?? "devsup-dev-only-data-protection-key-change-in-production";
var keyProtector = new AesGcmKeyProtector(dataProtectionKey);
builder.Services.AddSingleton<IKeyProtector>(keyProtector);

var github = builder.Configuration.GetSection("GitHub").Get<GitHubAuthSettings>() ?? new GitHubAuthSettings();
builder.Services.AddSingleton(github);
builder.Services.AddHttpClient<IGitHubGateway, GitHubGateway>(client => client.Timeout = TimeSpan.FromSeconds(15));

var gitlab = builder.Configuration.GetSection("GitLab").Get<GitLabAuthSettings>() ?? new GitLabAuthSettings();
builder.Services.AddSingleton(gitlab);
builder.Services.AddHttpClient<IGitLabGateway, GitLabGateway>(client => client.Timeout = TimeSpan.FromSeconds(15));

var aiEndpointResolver = new AiEndpointResolver(builder.Configuration);
builder.Services.AddSingleton(aiEndpointResolver);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IAiPatchGenerator, HttpAiPatchGenerator>();
builder.Services.AddSingleton<AiRepairProvider>();

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
builder.Services.AddHttpClient<IPullRequestGateway, PullRequestGateway>(client => client.Timeout = TimeSpan.FromSeconds(15));
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

    // Idempotently promote configured platform admins so the first admin always exists.
    var adminEmails = (builder.Configuration["Admin:Emails"] ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(e => e.ToLowerInvariant())
        .ToHashSet();

    if (adminEmails.Count > 0)
    {
        var admins = await db.Users.Where(u => adminEmails.Contains(u.Email)).ToListAsync();
        foreach (var admin in admins.Where(u => !u.IsAdmin))
        {
            db.Entry(admin).Property(u => u.IsAdmin).CurrentValue = true;
        }
        if (admins.Count > 0)
        {
            await db.SaveChangesAsync();
        }
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles(new DefaultFilesOptions
{
    RequestPath = "/dashboard",
    DefaultFileNames = { "index.html" }
});
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/dashboard",
    FileProvider = new PhysicalFileProvider(Path.Combine(app.Environment.ContentRootPath, "wwwroot"))
});

// Returns null when the caller may act on a repository (owner or operator member),
// or an IResult (404/403) to return immediately.
static async Task<IResult?> CheckTriagePermissionAsync(DevSupDbContext db, Guid repositoryId, Guid userId, CancellationToken ct)
{
    var isOwner = await db.ConnectedRepositories.AsNoTracking()
        .AnyAsync(r => r.Id == repositoryId && r.OwnerUserId == userId, ct);
    if (isOwner) return null;

    var membership = await db.RepositoryMembers.AsNoTracking()
        .FirstOrDefaultAsync(m => m.RepositoryId == repositoryId && m.UserId == userId, ct);
    if (membership is null)
    {
        return Results.NotFound();
    }
    if (membership.Role == MemberRole.Observer)
    {
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            detail: "Only the repository owner or operator members can perform this action.");
    }
    return null;
}

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

app.MapPost("/api/users/login", async (LoginRequest request, DevSupDbContext db, IPasswordHasherService hasher, JwtTokenIssuer issuer, AuditRecorder audit, CancellationToken ct) =>
{
    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == request.Email.Trim().ToLowerInvariant(), ct);
    if (user is null || !hasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
    {
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, detail: "Invalid email or password.");
    }

    if (!user.Active)
    {
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: "This account has been deactivated.");
    }

    var (token, expiresAt) = issuer.Issue(user);
    await audit.RecordAsync(user.Id, user.Email, "user.login", "User", user.Id.ToString(), ct: ct);
    return Results.Ok(new LoginResponse(token, expiresAt, new UserResponse(user.Id, user.Email, user.DisplayName)));
});

app.MapGet("/api/account", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var account = await db.Users.AsNoTracking()
        .SingleAsync(u => u.Id == user.GetUserId(), ct);
    return Results.Ok(new AccountResponse(account.Id, account.Email, account.DisplayName, account.IsAdmin, account.Active, account.DigestEnabled, account.CreatedAt));
}).RequireAuthorization();

app.MapPut("/api/account", async (UpdateAccountRequest request, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.DisplayName))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Display name is required.");
    }

    var account = await db.Users.FirstOrDefaultAsync(u => u.Id == user.GetUserId(), ct);
    if (account is null)
    {
        return Results.NotFound();
    }

    var before = account.DisplayName;
    var beforeDigests = account.DigestEnabled;
    db.Entry(account).Property(u => u.DisplayName).CurrentValue = request.DisplayName.Trim();
    if (request.DigestEnabled is not null && request.DigestEnabled.Value != beforeDigests)
    {
        db.Entry(account).Property(u => u.DigestEnabled).CurrentValue = request.DigestEnabled.Value;
    }
    await db.SaveChangesAsync(ct);
    await audit.RecordAsync(account.Id, account.Email, "account.profileUpdate", "User",
        account.Id.ToString(), before: before, after: account.DisplayName, ct: ct);

    return Results.Ok(new AccountResponse(account.Id, account.Email, account.DisplayName, account.IsAdmin, account.Active, account.DigestEnabled, account.CreatedAt));
}).RequireAuthorization();

app.MapPost("/api/account/password", async (ChangePasswordRequest request, ClaimsPrincipal user, DevSupDbContext db, IPasswordHasherService hasher, AuditRecorder audit, CancellationToken ct) =>
{
    var account = await db.Users.FirstOrDefaultAsync(u => u.Id == user.GetUserId(), ct);
    if (account is null)
    {
        return Results.NotFound();
    }

    if (!hasher.Verify(request.CurrentPassword ?? string.Empty, account.PasswordHash))
    {
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, detail: "Current password is incorrect.");
    }

    if (request.NewPassword is null || request.NewPassword.Length < 8)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Password must be at least 8 characters.");
    }

    db.Entry(account).Property(u => u.PasswordHash).CurrentValue = hasher.Hash(request.NewPassword);
    await db.SaveChangesAsync(ct);
    await audit.RecordAsync(account.Id, account.Email, "account.passwordChange", "User",
        account.Id.ToString(), ct: ct);

    return Results.NoContent();
}).RequireAuthorization();

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

app.MapGet("/api/auth/gitlab/login", (HttpContext http, IGitLabGateway gateway, GitLabAuthSettings gitlab) =>
{
    if (!gitlab.IsConfigured)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            detail: "GitLab OAuth is not configured. Set GitLab:ClientId and GitLab:ClientSecret.");
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

app.MapGet("/api/auth/gitlab/callback", async (
    string code,
    string? state,
    HttpContext http,
    IGitLabGateway gateway,
    GitLabAuthSettings gitlab,
    DevSupDbContext db,
    IPasswordHasherService hasher,
    JwtTokenIssuer issuer,
    IKeyProtector protector,
    CancellationToken ct) =>
{
    if (!gitlab.IsConfigured)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            detail: "GitLab OAuth is not configured.");
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

    GitLabTokenResult tokenResult;
    GitLabProfile profile;
    try
    {
        tokenResult = await gateway.ExchangeCodeAsync(code, ct);
        profile = await gateway.GetProfileAsync(tokenResult.AccessToken, ct);
    }
    catch (Exception ex)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
            detail: $"GitLab OAuth exchange failed: {ex.Message}");
    }

    if (string.IsNullOrWhiteSpace(profile.Email))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
            detail: "DevSup could not read an email from your GitLab profile. Make the email public on GitLab, then retry.");
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
            PasswordHash = hasher.Hash("gitlab-oauth-" + Guid.NewGuid().ToString("N")),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
    }

    var encrypted = protector.Protect(tokenResult.AccessToken);
    var existingToken = await db.OAuthTokens.FirstOrDefaultAsync(
        o => o.UserId == user.Id && o.Provider == GitProvider.GitLab, ct);

    if (existingToken is null)
    {
        db.OAuthTokens.Add(new OAuthToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Provider = GitProvider.GitLab,
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

app.MapGet("/api/ai-keys", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var keys = await db.AiModelKeyBindings.AsNoTracking()
        .Where(k => k.UserId == ownerId)
        .OrderBy(k => k.Provider)
        .ThenBy(k => k.Model)
        .Select(k => new AiKeyResponse(k.Provider, k.Model, k.KeyMask, k.UpdatedAt))
        .ToListAsync(ct);

    return Results.Ok(keys);
}).RequireAuthorization();

app.MapPost("/api/ai-keys", async (AiKeyRequest request, ClaimsPrincipal user, DevSupDbContext db, IKeyProtector protector, AuditRecorder audit, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var actor = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email })
        .SingleAsync(u => u.Id == ownerId, ct);

    if (string.IsNullOrWhiteSpace(request.Key) || request.Key.Length < 8)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "API key must be at least 8 characters.");
    }
    if (string.IsNullOrWhiteSpace(request.Model))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Model name is required.");
    }

    var encrypted = protector.Protect(request.Key);
    var mask = "••••••••" + request.Key[^4..];

    var existing = await db.AiModelKeyBindings.FirstOrDefaultAsync(
        k => k.UserId == ownerId && k.Provider == request.Provider && k.Model == request.Model, ct);
    var now = DateTimeOffset.UtcNow;

    if (existing is null)
    {
        var binding = new AiModelKeyBinding
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            Provider = request.Provider,
            Model = request.Model,
            EncryptedApiKey = encrypted,
            KeyMask = mask,
            UpdatedAt = now
        };
        db.AiModelKeyBindings.Add(binding);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(actor.Id, actor.Email, "aiKey.create", "AiModelKeyBinding",
            $"{request.Provider}:{request.Model}", after: mask, ct: ct);
        return Results.Created($"/api/ai-keys/{request.Provider}/{request.Model}",
            new AiKeyResponse(binding.Provider, binding.Model, binding.KeyMask, binding.UpdatedAt));
    }
    else
    {
        var entry = db.Entry(existing);
        entry.Property(k => k.EncryptedApiKey).CurrentValue = encrypted;
        entry.Property(k => k.KeyMask).CurrentValue = mask;
        entry.Property(k => k.UpdatedAt).CurrentValue = now;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(actor.Id, actor.Email, "aiKey.update", "AiModelKeyBinding",
            $"{request.Provider}:{request.Model}", before: existing.KeyMask, after: mask, ct: ct);
        return Results.Ok(new AiKeyResponse(existing.Provider, existing.Model, mask, now));
    }
}).RequireAuthorization();

app.MapDelete("/api/ai-keys", async (
    string provider,
    string model,
    ClaimsPrincipal user,
    DevSupDbContext db,
    AuditRecorder audit,
    CancellationToken ct) =>
{
    if (!Enum.TryParse<AiModelProvider>(provider, ignoreCase: true, out var parsedProvider))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: $"Unknown AI provider '{provider}'.");
    }

    var ownerId = user.GetUserId();
    var binding = await db.AiModelKeyBindings.FirstOrDefaultAsync(
        k => k.UserId == ownerId && k.Provider == parsedProvider && k.Model == model, ct);

    if (binding is null)
    {
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "No such key binding.");
    }

    var actor = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email })
        .SingleAsync(u => u.Id == ownerId, ct);
    db.AiModelKeyBindings.Remove(binding);
    await db.SaveChangesAsync(ct);
    await audit.RecordAsync(actor.Id, actor.Email, "aiKey.delete", "AiModelKeyBinding",
        $"{parsedProvider}:{model}", before: binding.KeyMask, ct: ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/repositories", async (CreateRepositoryRequest request, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
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
        RepairMode = request.RepairMode,
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

    var actor = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email })
        .SingleAsync(u => u.Id == ownerId, ct);
    await audit.RecordAsync(actor.Id, actor.Email, "repository.connect", "ConnectedRepository",
        repository.Id.ToString(), after: $"{request.CloneUrl} ({request.DefaultBranch})", ct: ct);

    return Results.Created($"/api/repositories/{repository.Id}",
        new RepositoryResponse(repository.Id, repository.Provider.ToString(), repository.CloneUrl, repository.DefaultBranch, repository.AppUrl, repository.RepairMode, repository.AppHealthy, repository.AppHealthCheckedAt, repository.AppHealthLastError));
}).RequireAuthorization();

app.MapGet("/api/repositories", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var repositories = await db.ConnectedRepositories
        .AsNoTracking()
        .Where(r => r.OwnerUserId == ownerId
            || db.RepositoryMembers.Any(m => m.RepositoryId == r.Id && m.UserId == ownerId))
        .Select(r => new RepositoryResponse(r.Id, r.Provider.ToString(), r.CloneUrl, r.DefaultBranch, r.AppUrl, r.RepairMode, r.AppHealthy, r.AppHealthCheckedAt, r.AppHealthLastError))
        .ToListAsync(ct);

    return Results.Ok(repositories);
}).RequireAuthorization();

app.MapPost("/api/repositories/{id:guid}/pause", async (Guid id, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var repository = await db.ConnectedRepositories
        .FirstOrDefaultAsync(r => r.Id == id && r.OwnerUserId == ownerId, ct);

    if (repository is null)
    {
        return Results.NotFound();
    }

    if (repository.Paused)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Repository monitoring is already paused.");
    }

    var now = DateTimeOffset.UtcNow;
    db.Entry(repository).Property(r => r.Paused).CurrentValue = true;
    db.Entry(repository).Property(r => r.PausedAt).CurrentValue = now;
    await db.SaveChangesAsync(ct);
    await audit.RecordAsync(ownerId, user.Identity?.Name ?? "", "repository.pause", "ConnectedRepository",
        repository.Id.ToString(), after: $"{repository.CloneUrl} paused", ct: ct);

    return Results.Ok(new { repositoryId = repository.Id, paused = true });
}).RequireAuthorization();

app.MapPost("/api/repositories/{id:guid}/unpause", async (Guid id, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var repository = await db.ConnectedRepositories
        .FirstOrDefaultAsync(r => r.Id == id && r.OwnerUserId == ownerId, ct);

    if (repository is null)
    {
        return Results.NotFound();
    }

    if (!repository.Paused)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Repository monitoring is not paused.");
    }

    db.Entry(repository).Property(r => r.Paused).CurrentValue = false;
    db.Entry(repository).Property(r => r.PausedAt).CurrentValue = null;
    await db.SaveChangesAsync(ct);
    await audit.RecordAsync(ownerId, user.Identity?.Name ?? "", "repository.unpause", "ConnectedRepository",
        repository.Id.ToString(), after: $"{repository.CloneUrl} resumed", ct: ct);

    return Results.Ok(new { repositoryId = repository.Id, paused = false });
}).RequireAuthorization();

app.MapGet("/api/repositories/{id:guid}/members", async (Guid id, ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var hasAccess = await db.ConnectedRepositories.AsNoTracking()
        .AnyAsync(r => r.Id == id
            && (r.OwnerUserId == ownerId || db.RepositoryMembers.Any(m => m.RepositoryId == r.Id && m.UserId == ownerId)), ct);
    if (!hasAccess)
    {
        return Results.NotFound();
    }

    var isOwner = await db.ConnectedRepositories.AsNoTracking()
        .AnyAsync(r => r.Id == id && r.OwnerUserId == ownerId, ct);

    var members = await (from m in db.RepositoryMembers.AsNoTracking()
                         join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                         where m.RepositoryId == id
                         orderby m.CreatedAt
                         select new RepositoryMemberResponse(u.Id, u.Email, u.DisplayName, m.Role.ToString(), m.CreatedAt))
        .ToListAsync(ct);

    return Results.Ok(new RepositoryMembersResponse(isOwner, members));
}).RequireAuthorization();

app.MapPost("/api/repositories/{id:guid}/members", async (Guid id, AddRepositoryMemberRequest request, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var repository = await db.ConnectedRepositories.AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == id && r.OwnerUserId == ownerId, ct);
    if (repository is null)
    {
        return Results.NotFound();
    }

    var member = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == request.Email, ct);
    if (member is null || member.Id == ownerId)
    {
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "No other registered user has that email.");
    }

    var role = request.Role switch
    {
        var r when string.Equals(r, "observer", StringComparison.OrdinalIgnoreCase) => MemberRole.Observer,
        var r when string.Equals(r, "operator", StringComparison.OrdinalIgnoreCase) => MemberRole.Operator,
        _ => (MemberRole?)null
    };
    if (role is null)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Role must be observer or operator.");
    }

    var resolvedRole = role.Value;

    var existing = await db.RepositoryMembers
        .FirstOrDefaultAsync(m => m.RepositoryId == id && m.UserId == member.Id, ct);
    if (existing is null)
    {
        db.RepositoryMembers.Add(new RepositoryMember
        {
            RepositoryId = id,
            UserId = member.Id,
            Role = resolvedRole,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }
    else
    {
        db.Entry(existing).Property(m => m.Role).CurrentValue = resolvedRole;
    }

    await db.SaveChangesAsync(ct);
    await audit.RecordAsync(ownerId, user.Identity?.Name ?? "", "repository.share", "RepositoryMember",
        $"{id}/{member.Id}", after: $"{member.Email} as {resolvedRole}", ct: ct);

    return Results.Ok(new RepositoryMemberResponse(member.Id, member.Email, member.DisplayName, resolvedRole.ToString(), existing?.CreatedAt ?? DateTimeOffset.UtcNow));
}).RequireAuthorization();

app.MapDelete("/api/repositories/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var owned = await db.ConnectedRepositories.AsNoTracking()
        .AnyAsync(r => r.Id == id && r.OwnerUserId == ownerId, ct);
    if (!owned)
    {
        return Results.NotFound();
    }

    var membership = await db.RepositoryMembers.FirstOrDefaultAsync(m => m.RepositoryId == id && m.UserId == userId, ct);
    if (membership is null)
    {
        return Results.NotFound();
    }

    var email = (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct))?.Email ?? userId.ToString();
    db.RepositoryMembers.Remove(membership);
    await db.SaveChangesAsync(ct);
    await audit.RecordAsync(ownerId, user.Identity?.Name ?? "", "repository.unshare", "RepositoryMember",
        $"{id}/{userId}", before: email, ct: ct);

    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/repositories/{id:guid}/transfer", async (Guid id, TransferRepositoryRequest request, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var repository = await db.ConnectedRepositories
        .FirstOrDefaultAsync(r => r.Id == id && r.OwnerUserId == ownerId, ct);
    if (repository is null)
    {
        return Results.NotFound();
    }

    var target = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == request.Email, ct);
    if (target is null || target.Id == ownerId)
    {
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "No other registered user has that email.");
    }

    var membership = await db.RepositoryMembers
        .FirstOrDefaultAsync(m => m.RepositoryId == id && m.UserId == target.Id, ct);
    if (membership is null || membership.Role != MemberRole.Operator)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
            detail: "Ownership can only be transferred to an existing operator member.");
    }

    var collides = await db.ConnectedRepositories.AsNoTracking()
        .AnyAsync(r => r.OwnerUserId == target.Id && r.CloneUrl == repository.CloneUrl, ct);
    if (collides)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict,
            detail: "The new owner already has a repository with this clone URL.");
    }

    var previousOwner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ownerId, ct);
    db.Entry(repository).Property(r => r.OwnerUserId).CurrentValue = target.Id;
    db.RepositoryMembers.Remove(membership);

    var previousOwnerMembership = await db.RepositoryMembers
        .FirstOrDefaultAsync(m => m.RepositoryId == id && m.UserId == ownerId, ct);
    if (previousOwnerMembership is null)
    {
        db.RepositoryMembers.Add(new RepositoryMember
        {
            RepositoryId = id,
            UserId = ownerId,
            Role = MemberRole.Operator,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }
    else
    {
        db.Entry(previousOwnerMembership).Property(m => m.Role).CurrentValue = MemberRole.Operator;
    }

    db.EmailMessages.Add(new EmailMessage
    {
        Id = Guid.NewGuid(),
        UserId = target.Id,
        To = target.Email,
        Subject = $"DevSup: you now own {repository.CloneUrl}",
        HtmlBody = $"<p><strong>{System.Net.WebUtility.HtmlEncode(previousOwner?.Email ?? "The previous owner")}</strong> transferred ownership of " +
                   $"<code>{System.Net.WebUtility.HtmlEncode(repository.CloneUrl)}</code> to you.</p>" +
                   $"<p>You can now manage its members, pause/resume monitoring, and configure its app URL. " +
                   $"Make sure a git provider token is linked so the repair agent can push fixes.</p>",
        CreatedAt = DateTimeOffset.UtcNow
    });

    await db.SaveChangesAsync(ct);
    await audit.RecordAsync(ownerId, user.Identity?.Name ?? "", "repository.transfer", "ConnectedRepository",
        repository.Id.ToString(), before: previousOwner?.Email, after: target.Email, ct: ct);

    return Results.Ok(new { repositoryId = repository.Id, ownerUserId = target.Id, ownerEmail = target.Email });
}).RequireAuthorization();

app.MapPost("/api/repositories/{id:guid}/leave", async (Guid id, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var userId = user.GetUserId();
    var repository = await db.ConnectedRepositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    if (repository is null)
    {
        return Results.NotFound();
    }
    if (repository.OwnerUserId == userId)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict,
            detail: "Transfer ownership to another member before leaving your own repository.");
    }

    var membership = await db.RepositoryMembers.FirstOrDefaultAsync(m => m.RepositoryId == id && m.UserId == userId, ct);
    if (membership is null)
    {
        return Results.NotFound();
    }

    var email = (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct))?.Email ?? userId.ToString();
    db.RepositoryMembers.Remove(membership);
    await db.SaveChangesAsync(ct);
    await audit.RecordAsync(userId, user.Identity?.Name ?? "", "repository.leave", "RepositoryMember",
        $"{id}/{userId}", before: email, ct: ct);

    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/ingest", async (IngestFailureRequest request, HttpRequest httpRequest, ClaimsPrincipal user, DevSupDbContext db, IFailureClassifier classifier, CancellationToken ct) =>
{
    if (httpRequest.Headers.TryGetValue(PayloadSanitizer.SchemaVersionHeaderName, out var versionHeader)
        && int.TryParse(versionHeader, out var reportedVersion)
        && reportedVersion > PayloadSanitizer.CurrentSchemaVersion)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status426UpgradeRequired,
            title: "Upgrade Required",
            detail: $"Schema version {reportedVersion} is newer than the {PayloadSanitizer.CurrentSchemaVersion} this platform supports. Upgrade the DevSup middleware/consumer before reporting.");
    }

    var ownerId = user.GetUserId();

    var repository = await db.ConnectedRepositories.AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == request.RepositoryId && r.OwnerUserId == ownerId, ct);

    if (repository is null)
    {
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Repository not found for this account.");
    }

    if (repository.Paused)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict,
            detail: "Repository monitoring is paused — unpause it before ingesting failures.");
    }

    var failure = new FailureEvent
    {
        Id = Guid.NewGuid(),
        RepositoryId = repository.Id,
        StatusCode = request.StatusCode,
        Method = request.Method,
        Path = request.Path,
        RequestPayload = Truncate(PayloadSanitizer.Redact(request.RequestPayload), 8192),
        ResponsePayload = Truncate(PayloadSanitizer.Redact(request.ResponsePayload), 8192),
        ExceptionMessage = Truncate(PayloadSanitizer.Redact(request.ExceptionMessage), 4096),
        StackTrace = Truncate(PayloadSanitizer.Redact(request.StackTrace), 16_384),
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
        await FailureReporter.Notify(db, owner, repository, failure, ticket, category, ct: ct);
    }

    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/tickets/{ticket.Id}",
        new IngestResponse(failure.Id, ticket.Id, category.ToString(), kind.ToString(), status.ToString()));
}).RequireAuthorization();

app.MapPost("/api/failures/{failureId:guid}/replay", async (Guid failureId, ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();

    var failure = await db.FailureEvents.AsNoTracking()
        .FirstOrDefaultAsync(f => f.Id == failureId
            && db.ConnectedRepositories.Any(r => r.Id == f.RepositoryId && r.OwnerUserId == ownerId), ct);

    if (failure is null)
    {
        return Results.NotFound();
    }

    var repository = await db.ConnectedRepositories.AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == failure.RepositoryId && r.OwnerUserId == ownerId, ct);
    var ticket = await db.RepairTickets.AsNoTracking()
        .FirstOrDefaultAsync(t => t.FailureEventId == failure.Id, ct);
    var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ownerId, ct);

    if (repository is null || ticket is null || owner is null)
    {
        return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
            detail: "Failure is missing its repository, ticket, or owner.");
    }

    await FailureReporter.Notify(db, owner, repository, failure, ticket, ticket.Category, replay: true, ct: ct);

    var emailsQueued = db.ChangeTracker.Entries<EmailMessage>().Count();
    var webhooksQueued = db.ChangeTracker.Entries<WebhookDelivery>().Count();
    await db.SaveChangesAsync(ct);

    return Results.Accepted(null, new { failureId = failure.Id, emailsQueued, webhooksQueued });
}).RequireAuthorization();

app.MapPost("/api/tickets/{ticketId:guid}/redispatch", async (Guid ticketId, ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();

    var ticket = await db.RepairTickets
        .FirstOrDefaultAsync(t => t.Id == ticketId
            && db.ConnectedRepositories.Any(r => r.Id == t.RepositoryId && (r.OwnerUserId == ownerId || db.RepositoryMembers.Any(m => m.RepositoryId == r.Id && m.UserId == ownerId))), ct);

    if (ticket is null)
    {
        return Results.NotFound();
    }

    if (ticket.Status is not (TicketStatus.New or TicketStatus.NeedsHumanReview))
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict,
            detail: "Only new or needs-human-review tickets can be re-dispatched.");
    }

    var redispatchDeny = await CheckTriagePermissionAsync(db, ticket.RepositoryId, ownerId, ct);
    if (redispatchDeny is not null)
    {
        return redispatchDeny;
    }

    var now = DateTimeOffset.UtcNow;
    db.Entry(ticket).Property(t => t.Status).CurrentValue = TicketStatus.New;
    db.Entry(ticket).Property(t => t.UpdatedAt).CurrentValue = now;
    await db.SaveChangesAsync(ct);

    return Results.Accepted(null, new { ticketId = ticket.Id, status = "new" });
}).RequireAuthorization();

app.MapGet("/api/tickets", async (Guid? repositoryId, ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();

    var tickets = (await (
            from t in db.RepairTickets.AsNoTracking()
            join f in db.FailureEvents.AsNoTracking() on t.FailureEventId equals f.Id
            where db.ConnectedRepositories.Any(r => r.Id == t.RepositoryId
                && (r.OwnerUserId == ownerId || db.RepositoryMembers.Any(m => m.RepositoryId == r.Id && m.UserId == ownerId)))
            select new { Ticket = t, Failure = f }).ToListAsync(ct))
        .Where(x => repositoryId is null || x.Ticket.RepositoryId == repositoryId)
        .OrderByDescending(x => x.Ticket.UpdatedAt)
        .Select(x => new TicketResponse(
            x.Ticket.Id,
            x.Ticket.FailureEventId,
            x.Ticket.RepositoryId,
            x.Ticket.Category.ToString(),
            x.Ticket.Kind.ToString(),
            x.Ticket.Status.ToString(),
            x.Ticket.Analysis,
            x.Ticket.PatchSummary,
            x.Ticket.CommitSha,
            x.Ticket.PullRequestUrl,
            x.Ticket.UpdatedAt,
            x.Failure.Method,
            x.Failure.Path,
            x.Failure.StatusCode))
        .ToList();

    return Results.Ok(tickets);
}).RequireAuthorization();

app.MapGet("/api/tickets/{ticketId:guid}", async (Guid ticketId, ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();

    var ticket = await (from t in db.RepairTickets.AsNoTracking()
                        join f in db.FailureEvents.AsNoTracking() on t.FailureEventId equals f.Id
where t.Id == ticketId
                        && db.ConnectedRepositories.Any(r => r.Id == t.RepositoryId
                            && (r.OwnerUserId == ownerId || db.RepositoryMembers.Any(m => m.RepositoryId == r.Id && m.UserId == ownerId)))
                        select new { Ticket = t, Failure = f }).FirstOrDefaultAsync(ct);

    if (ticket is null)
    {
        return Results.NotFound();
    }

    var canTriage = (await CheckTriagePermissionAsync(db, ticket.Ticket.RepositoryId, ownerId, ct)) is null;

    var row = ticket; // row = { Ticket, Failure }
    return Results.Ok(new TicketDetailResponse(
        row.Ticket.Id, row.Ticket.FailureEventId, row.Ticket.RepositoryId, row.Ticket.Category.ToString(), row.Ticket.Kind.ToString(),
        row.Ticket.Status.ToString(), row.Ticket.Analysis, row.Ticket.PatchSummary, row.Ticket.CommitSha, row.Ticket.PullRequestUrl, row.Ticket.LastError,
        row.Ticket.UpdatedAt, row.Failure.Method, row.Failure.Path, row.Failure.StatusCode, row.Failure.ExceptionMessage, row.Failure.OccurredAt,
        CanTriage: canTriage));
}).RequireAuthorization();

app.MapPost("/api/tickets/{ticketId:guid}/close", async (Guid ticketId, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();

    var ticket = await db.RepairTickets
        .FirstOrDefaultAsync(t => t.Id == ticketId
            && db.ConnectedRepositories.Any(r => r.Id == t.RepositoryId && (r.OwnerUserId == ownerId || db.RepositoryMembers.Any(m => m.RepositoryId == r.Id && m.UserId == ownerId))), ct);

    if (ticket is null)
    {
        return Results.NotFound();
    }

    if (ticket.Status == TicketStatus.Closed)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "The ticket is already closed.");
    }

    var closeDeny = await CheckTriagePermissionAsync(db, ticket.RepositoryId, ownerId, ct);
    if (closeDeny is not null)
    {
        return closeDeny;
    }

    var before = ticket.Status.ToString();
    var now = DateTimeOffset.UtcNow;
    db.Entry(ticket).Property(t => t.Status).CurrentValue = TicketStatus.Closed;
    db.Entry(ticket).Property(t => t.UpdatedAt).CurrentValue = now;
    await db.SaveChangesAsync(ct);
    await audit.RecordAsync(ownerId, user.Identity?.Name ?? "", "ticket.close", "RepairTicket",
        ticket.Id.ToString(), before: before, after: TicketStatus.Closed.ToString(), ct: ct);

    return Results.Ok(new { ticketId = ticket.Id, status = "closed" });
}).RequireAuthorization();

app.MapPost("/api/tickets/{ticketId:guid}/reopen", async (Guid ticketId, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();

    var ticket = await db.RepairTickets
        .FirstOrDefaultAsync(t => t.Id == ticketId
            && db.ConnectedRepositories.Any(r => r.Id == t.RepositoryId && (r.OwnerUserId == ownerId || db.RepositoryMembers.Any(m => m.RepositoryId == r.Id && m.UserId == ownerId))), ct);

    if (ticket is null)
    {
        return Results.NotFound();
    }

    if (ticket.Status != TicketStatus.Closed)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Only closed tickets can be reopened.");
    }

    var reopenDeny = await CheckTriagePermissionAsync(db, ticket.RepositoryId, ownerId, ct);
    if (reopenDeny is not null)
    {
        return reopenDeny;
    }

    var before = ticket.Status.ToString();
    var now = DateTimeOffset.UtcNow;
    db.Entry(ticket).Property(t => t.Status).CurrentValue = TicketStatus.New;
    db.Entry(ticket).Property(t => t.UpdatedAt).CurrentValue = now;
    await db.SaveChangesAsync(ct);
    await audit.RecordAsync(ownerId, user.Identity?.Name ?? "", "ticket.reopen", "RepairTicket",
        ticket.Id.ToString(), before: before, after: TicketStatus.New.ToString(), ct: ct);

    return Results.Ok(new { ticketId = ticket.Id, status = "new" });
}).RequireAuthorization();

app.MapGet("/api/overview", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();

    var repositories = await db.ConnectedRepositories
        .AsNoTracking()
        .Where(r => r.OwnerUserId == ownerId
            || db.RepositoryMembers.Any(m => m.RepositoryId == r.Id && m.UserId == ownerId))
        .Select(r => new RepositoryHealthRow(r.Id, r.CloneUrl, r.AppUrl, r.AppHealthy, r.AppHealthCheckedAt, r.AppHealthLastError, r.Paused, r.PausedAt))
        .ToListAsync(ct);

    var tickets = await db.RepairTickets
        .AsNoTracking()
        .Where(t => db.ConnectedRepositories.Any(r => r.Id == t.RepositoryId
            && (r.OwnerUserId == ownerId || db.RepositoryMembers.Any(m => m.RepositoryId == r.Id && m.UserId == ownerId))))
        .ToListAsync(ct);

    var counts = new TicketSummary(
        New: tickets.Count(t => t.Status == TicketStatus.New),
        InProgress: tickets.Count(t => t.Status is TicketStatus.Triaged or TicketStatus.Investigating or TicketStatus.PatchProposed),
        PendingReview: tickets.Count(t => t.Status == TicketStatus.FixPendingReview),
        Fixed: tickets.Count(t => t.Status is TicketStatus.FixPushed or TicketStatus.FixVerified or TicketStatus.Closed),
        NeedsHumanReview: tickets.Count(t => t.Status == TicketStatus.NeedsHumanReview),
        Total: tickets.Count);

    return Results.Ok(new OverviewResponse(
        RepositoryCount: repositories.Count,
        HealthyRepos: repositories.Count(r => r.AppHealthy == true),
        UnhealthyRepos: repositories.Count(r => r.AppHealthy == false),
        UncheckedRepos: repositories.Count(r => r.AppHealthy is null),
        Repositories: repositories,
        Tickets: counts));
}).RequireAuthorization();

app.MapGet("/api/failures", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct,
    Guid? repositoryId = null, DateTimeOffset? from = null, DateTimeOffset? to = null,
    string? status = null, int page = 1, int pageSize = 25) =>
{
    var ownerId = user.GetUserId();
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);

    var query =
        from f in db.FailureEvents.AsNoTracking()
        join t in db.RepairTickets.AsNoTracking() on f.Id equals t.FailureEventId into tj
        from t in tj.DefaultIfEmpty()
        where db.ConnectedRepositories.Any(r => r.Id == f.RepositoryId
                && (r.OwnerUserId == ownerId || db.RepositoryMembers.Any(m => m.RepositoryId == r.Id && m.UserId == ownerId)))
        select new { Failure = f, Ticket = t };
    if (repositoryId is not null)
    {
        query = query.Where(x => x.Failure.RepositoryId == repositoryId);
    }
    if (from is not null)
    {
        query = query.Where(x => x.Failure.OccurredAt >= from);
    }
    if (to is not null)
    {
        query = query.Where(x => x.Failure.OccurredAt < to);
    }
    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<TicketStatus>(status, ignoreCase: true, out var parsedStatus))
    {
        query = query.Where(x => x.Ticket != null && x.Ticket.Status == parsedStatus);
    }

    var total = await query.CountAsync(ct);
    var rows = await query
        .OrderByDescending(x => x.Failure.OccurredAt)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(x => new FailureHistoryRow(
            x.Failure.Id,
            x.Failure.RepositoryId,
            x.Failure.StatusCode,
            x.Failure.Method,
            x.Failure.Path,
            x.Failure.ExceptionMessage,
            x.Failure.OccurredAt,
            x.Ticket != null ? x.Ticket.Id : null,
            x.Ticket != null ? x.Ticket.Status.ToString() : null,
            x.Ticket != null ? x.Ticket.Category.ToString() : null,
            x.Ticket != null ? x.Ticket.Kind.ToString() : null,
            x.Ticket != null ? x.Ticket.PatchSummary : null,
            x.Ticket != null ? x.Ticket.CommitSha : null))
        .ToListAsync(ct);

    return Results.Ok(new FailureHistoryPage(rows, page, pageSize, total));
}).RequireAuthorization();

app.MapGet("/api/failures/export", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct,
    Guid? repositoryId = null, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
{
    var ownerId = user.GetUserId();

    var query =
        from f in db.FailureEvents.AsNoTracking()
        join t in db.RepairTickets.AsNoTracking() on f.Id equals t.FailureEventId into tj
        from t in tj.DefaultIfEmpty()
        where db.ConnectedRepositories.Any(r => r.Id == f.RepositoryId
                && (r.OwnerUserId == ownerId || db.RepositoryMembers.Any(m => m.RepositoryId == r.Id && m.UserId == ownerId)))
        select new { Failure = f, Ticket = t };
    if (repositoryId is not null)
    {
        query = query.Where(x => x.Failure.RepositoryId == repositoryId);
    }
    if (from is not null)
    {
        query = query.Where(x => x.Failure.OccurredAt >= from);
    }
    if (to is not null)
    {
        query = query.Where(x => x.Failure.OccurredAt < to);
    }

    var rows = await query.OrderBy(x => x.Failure.OccurredAt).ToListAsync(ct);

    var csv = new StringBuilder();
    csv.AppendLine("occurredAtUtc,repositoryId,method,path,statusCode,category,kind,ticketStatus,exceptionMessage,patchSummary,commitSha");
    foreach (var row in rows)
    {
        csv.Append(CsvEscape(row.Failure.OccurredAt.ToString("O"))).Append(',');
        csv.Append(CsvEscape(row.Failure.RepositoryId.ToString())).Append(',');
        csv.Append(CsvEscape(row.Failure.Method)).Append(',');
        csv.Append(CsvEscape(row.Failure.Path)).Append(',');
        csv.Append(row.Failure.StatusCode).Append(',');
        csv.Append(CsvEscape(row.Ticket?.Category.ToString())).Append(',');
        csv.Append(CsvEscape(row.Ticket?.Kind.ToString())).Append(',');
        csv.Append(CsvEscape(row.Ticket?.Status.ToString())).Append(',');
        csv.Append(CsvEscape(row.Failure.ExceptionMessage)).Append(',');
        csv.Append(CsvEscape(row.Ticket?.PatchSummary)).Append(',');
        csv.AppendLine(CsvEscape(row.Ticket?.CommitSha));
    }

    var bytes = Encoding.UTF8.GetBytes(csv.ToString());
    return Results.File(bytes, "text/csv", $"devsup-failures-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
}).RequireAuthorization();

app.MapGet("/api/admin/overview", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    if (!await IsAdminAsync(user, db, ct))
    {
        return Results.Forbid();
    }

    var totalUsers = await db.Users.CountAsync(ct);
    var activeUsers = await db.Users.CountAsync(u => u.Active, ct);
    var repositories = await db.ConnectedRepositories.CountAsync(ct);
    var failures = await db.FailureEvents.CountAsync(ct);
    var openTickets = await db.RepairTickets.CountAsync(
        t => t.Status != TicketStatus.FixPushed && t.Status != TicketStatus.FixVerified
             && t.Status != TicketStatus.Closed, ct);
    var webhooks = await db.WebhookEndpoints.CountAsync(ct);

    return Results.Ok(new AdminOverviewResponse(totalUsers, activeUsers, repositories, failures, openTickets, webhooks));
}).RequireAuthorization();

app.MapGet("/api/admin/users", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    if (!await IsAdminAsync(user, db, ct))
    {
        return Results.Forbid();
    }

    var rows = await (from u in db.Users.AsNoTracking()
                      let repositories = db.ConnectedRepositories.Count(r => r.OwnerUserId == u.Id)
                      let tickets = db.RepairTickets.Count(t => db.ConnectedRepositories.Any(r => r.Id == t.RepositoryId && r.OwnerUserId == u.Id))
                      select new
                      {
                          u.Id,
                          u.Email,
                          u.DisplayName,
                          u.IsAdmin,
                          u.Active,
                          u.CreatedAt,
                          repositories,
                          tickets
                      }).OrderBy(x => x.Email).ToListAsync(ct);

    return Results.Ok(rows.Select(x => new AdminUserResponse(
        x.Id, x.Email, x.DisplayName, x.IsAdmin, x.Active, x.CreatedAt, x.repositories, x.tickets)));
}).RequireAuthorization();

app.MapGet("/api/admin/audit", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct,
    string? actor = null, string? action = null, string? entityType = null) =>
{
    if (!await IsAdminAsync(user, db, ct))
    {
        return Results.Forbid();
    }

    var query = db.AuditEntries.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(actor))
    {
        query = query.Where(a => a.ActorEmail.Contains(actor));
    }
    if (!string.IsNullOrWhiteSpace(action))
    {
        query = query.Where(a => a.Action == action);
    }
    if (!string.IsNullOrWhiteSpace(entityType))
    {
        query = query.Where(a => a.EntityType == entityType);
    }

    var rows = await query
        .OrderByDescending(a => a.Timestamp)
        .Take(200)
        .Select(a => new AuditEntryResponse(
            a.Id, a.ActorEmail, a.Action, a.EntityType, a.EntityId, a.Before, a.After, a.IpAddress, a.Timestamp))
        .ToListAsync(ct);

    return Results.Ok(rows);
}).RequireAuthorization();

app.MapPost("/api/admin/users/{id:guid}/deactivate", async (Guid id, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    if (!await IsAdminAsync(user, db, ct))
    {
        return Results.Forbid();
    }

    var target = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    if (target is null)
    {
        return Results.NotFound();
    }

    db.Entry(target).Property(u => u.Active).CurrentValue = false;
    await db.SaveChangesAsync(ct);
    var actor = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email })
        .SingleAsync(u => u.Id == user.GetUserId(), ct);
    await audit.RecordAsync(actor.Id, actor.Email, "user.deactivate", "User", target.Id.ToString(),
        before: true.ToString(), after: false.ToString(), ct: ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/admin/users/{id:guid}/activate", async (Guid id, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    if (!await IsAdminAsync(user, db, ct))
    {
        return Results.Forbid();
    }

    var target = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    if (target is null)
    {
        return Results.NotFound();
    }

    db.Entry(target).Property(u => u.Active).CurrentValue = true;
    await db.SaveChangesAsync(ct);
    var actor = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email })
        .SingleAsync(u => u.Id == user.GetUserId(), ct);
    await audit.RecordAsync(actor.Id, actor.Email, "user.activate", "User", target.Id.ToString(),
        before: false.ToString(), after: true.ToString(), ct: ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/webhooks", async (CreateWebhookRequest request, ClaimsPrincipal user, DevSupDbContext db, IKeyProtector protector, AuditRecorder audit, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Url) || !IsAbsoluteHttpUrl(request.Url))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "A valid http(s) URL is required.");
    }

    var ownerId = user.GetUserId();

    if (await db.WebhookEndpoints.AnyAsync(w => w.UserId == ownerId && w.Url == request.Url, ct))
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "A webhook for this URL already exists.");
    }

    var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    var endpoint = new WebhookEndpoint
    {
        Id = Guid.NewGuid(),
        UserId = ownerId,
        Url = request.Url,
        Name = string.IsNullOrWhiteSpace(request.Name) ? null : Truncate(request.Name.Trim(), 128),
        Channel = request.Channel,
        EncryptedSecret = protector.Protect(secret),
        EventMask = EventsToMask(request.Events),
        CreatedAt = DateTimeOffset.UtcNow
    };

    db.WebhookEndpoints.Add(endpoint);
    await db.SaveChangesAsync(ct);

    var wactor = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email })
        .SingleAsync(u => u.Id == ownerId, ct);
    await audit.RecordAsync(wactor.Id, wactor.Email, "webhook.create", "WebhookEndpoint",
        endpoint.Id.ToString(), after: endpoint.Url, ct: ct);

    return Results.Created($"/api/webhooks/{endpoint.Id}",
        new CreateWebhookResponse(endpoint.Id, endpoint.Url, secret, ResolveEvents(endpoint.EventMask), endpoint.CreatedAt, endpoint.Name, endpoint.Channel));
}).RequireAuthorization();

app.MapGet("/api/webhooks", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();

    var webhooks = (await db.WebhookEndpoints
            .AsNoTracking()
            .Where(w => w.UserId == ownerId)
            .OrderBy(w => w.CreatedAt)
            .ToListAsync(ct))
        .Select(w => new WebhookResponse(w.Id, w.Url, ResolveEvents(w.EventMask), w.Active, w.CreatedAt, w.Name, w.Channel))
        .ToList();

    return Results.Ok(webhooks);
}).RequireAuthorization();

app.MapGet("/api/webhooks/{id:guid}/deliveries", async (Guid id, ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct,
    int page = 1, int pageSize = 20) =>
{
    var owns = await db.WebhookEndpoints.AsNoTracking()
        .AnyAsync(w => w.Id == id && w.UserId == user.GetUserId(), ct);
    if (!owns)
    {
        return Results.NotFound();
    }

    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);

    var total = await db.WebhookDeliveries.CountAsync(d => d.WebhookId == id, ct);
    var rows = await db.WebhookDeliveries
        .AsNoTracking()
        .Where(d => d.WebhookId == id)
        .OrderByDescending(d => d.CreatedAt)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(d => new WebhookDeliveryResponse(d.Id, d.Event.ToString(), d.Sent, d.SentAt, d.Attempts, d.LastError, d.CreatedAt))
        .ToListAsync(ct);

    return Results.Ok(new WebhookDeliveryPage(rows, page, pageSize, total));
}).RequireAuthorization();

app.MapDelete("/api/webhooks/{id:guid}", async (Guid id, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var webhook = await db.WebhookEndpoints.FirstOrDefaultAsync(w => w.Id == id && w.UserId == user.GetUserId(), ct);
    if (webhook is null)
    {
        return Results.NotFound();
    }

    db.WebhookEndpoints.Remove(webhook);
    await db.SaveChangesAsync(ct);
    var actor = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email })
        .SingleAsync(u => u.Id == user.GetUserId(), ct);
    await audit.RecordAsync(actor.Id, actor.Email, "webhook.delete", "WebhookEndpoint",
        webhook.Id.ToString(), before: webhook.Url, ct: ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/webhooks/{id:guid}/deactivate", async (Guid id, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var webhook = await db.WebhookEndpoints.FirstOrDefaultAsync(w => w.Id == id && w.UserId == user.GetUserId(), ct);
    if (webhook is null)
    {
        return Results.NotFound();
    }

    if (!webhook.Active)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Webhook endpoint is already inactive.");
    }

    db.Entry(webhook).Property(w => w.Active).CurrentValue = false;
    await db.SaveChangesAsync(ct);
    var actor = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email })
        .SingleAsync(u => u.Id == user.GetUserId(), ct);
    await audit.RecordAsync(actor.Id, actor.Email, "webhook.deactivate", "WebhookEndpoint",
        webhook.Id.ToString(), before: "active", after: "inactive", ct: ct);

    return Results.Ok(new { id = webhook.Id, active = false });
}).RequireAuthorization();

app.MapPost("/api/webhooks/{id:guid}/activate", async (Guid id, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var webhook = await db.WebhookEndpoints.FirstOrDefaultAsync(w => w.Id == id && w.UserId == user.GetUserId(), ct);
    if (webhook is null)
    {
        return Results.NotFound();
    }

    if (webhook.Active)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Webhook endpoint is already active.");
    }

    db.Entry(webhook).Property(w => w.Active).CurrentValue = true;
    await db.SaveChangesAsync(ct);
    var actor = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email })
        .SingleAsync(u => u.Id == user.GetUserId(), ct);
    await audit.RecordAsync(actor.Id, actor.Email, "webhook.activate", "WebhookEndpoint",
        webhook.Id.ToString(), before: "inactive", after: "active", ct: ct);

    return Results.Ok(new { id = webhook.Id, active = true });
}).RequireAuthorization();

app.MapPost("/api/webhooks/{id:guid}/rotate", async (Guid id, ClaimsPrincipal user, DevSupDbContext db, IKeyProtector protector, AuditRecorder audit, CancellationToken ct) =>
{
    var webhook = await db.WebhookEndpoints.FirstOrDefaultAsync(w => w.Id == id && w.UserId == user.GetUserId(), ct);
    if (webhook is null)
    {
        return Results.NotFound();
    }

    var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    db.Entry(webhook).Property(w => w.EncryptedSecret).CurrentValue = protector.Protect(secret);
    await db.SaveChangesAsync(ct);

    var actor = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email })
        .SingleAsync(u => u.Id == user.GetUserId(), ct);
    await audit.RecordAsync(actor.Id, actor.Email, "webhook.rotate", "WebhookEndpoint",
        webhook.Id.ToString(), before: webhook.Url, ct: ct);

    return Results.Ok(new CreateWebhookResponse(webhook.Id, webhook.Url, secret, ResolveEvents(webhook.EventMask), webhook.CreatedAt, webhook.Name, webhook.Channel));
}).RequireAuthorization();

app.MapPost("/api/webhooks/{id:guid}/test", async (Guid id, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var webhook = await db.WebhookEndpoints.AsNoTracking()
        .FirstOrDefaultAsync(w => w.Id == id && w.UserId == ownerId, ct);
    if (webhook is null)
    {
        return Results.NotFound();
    }

    var payload = new
    {
        @event = "devsup.ping",
        webhookId = webhook.Id,
        webhookUrl = webhook.Url,
        webhookName = webhook.Name,
        channel = webhook.Channel.ToString().ToLowerInvariant(),
        timestamp = DateTimeOffset.UtcNow
    };
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var delivery = new WebhookDelivery
    {
        Id = Guid.NewGuid(),
        UserId = ownerId,
        WebhookId = webhook.Id,
        Event = WebhookEvent.Ping,
        Payload = json,
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.WebhookDeliveries.Add(delivery);
    await db.SaveChangesAsync(ct);

    var actor = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email })
        .SingleAsync(u => u.Id == ownerId, ct);
    await audit.RecordAsync(actor.Id, actor.Email, "webhook.test", "WebhookEndpoint",
        webhook.Id.ToString(), after: webhook.Url, ct: ct);

    return Results.Accepted(
        $"/api/webhooks/{webhook.Id}/deliveries/{delivery.Id}",
        new WebhookDeliveryResponse(delivery.Id, delivery.Event.ToString(), delivery.Sent, delivery.SentAt, delivery.Attempts, delivery.LastError, delivery.CreatedAt));
}).RequireAuthorization();

app.MapPost("/api/webhooks/{id:guid}/deliveries/{deliveryId:guid}/retry", async (Guid id, Guid deliveryId, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var delivery = await db.WebhookDeliveries
        .FirstOrDefaultAsync(d => d.Id == deliveryId && d.WebhookId == id && d.UserId == ownerId, ct);
    if (delivery is null)
    {
        return Results.NotFound();
    }

    if (delivery.Sent)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "This delivery was already sent.");
    }

    db.Entry(delivery).Property(d => d.Attempts).CurrentValue = 0;
    db.Entry(delivery).Property(d => d.LastError).CurrentValue = null;
    await db.SaveChangesAsync(ct);

    var actor = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email })
        .SingleAsync(u => u.Id == ownerId, ct);
    await audit.RecordAsync(actor.Id, actor.Email, "webhook.retry", "WebhookDelivery",
        delivery.Id.ToString(), before: delivery.LastError, ct: ct);

    return Results.Ok(new WebhookDeliveryResponse(delivery.Id, delivery.Event.ToString(), delivery.Sent, delivery.SentAt, delivery.Attempts, delivery.LastError, delivery.CreatedAt));
}).RequireAuthorization();

app.MapGet("/api/notification-preferences", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var rows = await (from p in db.NotificationPreferences.AsNoTracking()
                      join r in db.ConnectedRepositories.AsNoTracking() on p.RepositoryId equals r.Id
                      where p.UserId == ownerId
                      select new { p.RepositoryId, r.CloneUrl, p.EmailEnabled, p.MutedEmailEvents, p.UpdatedAt })
        .ToListAsync(ct);

    var result = rows.Select(row => new NotificationPreferenceResponse(
            row.RepositoryId,
            row.CloneUrl,
            row.EmailEnabled,
            Enumerable.Range(0, 8).Where(i => (row.MutedEmailEvents & (1 << i)) != 0).Select(i => ((WebhookEvent)i).ToString()).ToList(),
            row.UpdatedAt))
        .ToList();
    return Results.Ok(result);
}).RequireAuthorization();

app.MapPut("/api/notification-preferences", async (NotificationPreferenceRequest request, ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var repoOwned = await db.ConnectedRepositories.AsNoTracking()
        .AnyAsync(r => r.Id == request.RepositoryId
            && (r.OwnerUserId == ownerId || db.RepositoryMembers.Any(m => m.RepositoryId == r.Id && m.UserId == ownerId)), ct);
    if (!repoOwned)
    {
        return Results.NotFound();
    }

    var muted = 0;
    if (request.MutedEvents is not null)
    {
        foreach (var raw in request.MutedEvents)
        {
            if (!Enum.TryParse<WebhookEvent>(raw, ignoreCase: true, out var parsed))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: $"Unknown event '{raw}'.");
            }
            muted |= 1 << (int)parsed;
        }
    }

    var now = DateTimeOffset.UtcNow;
    var existing = await db.NotificationPreferences
        .FirstOrDefaultAsync(p => p.UserId == ownerId && p.RepositoryId == request.RepositoryId, ct);
    if (existing is null)
    {
        existing = new NotificationPreference
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            RepositoryId = request.RepositoryId,
            EmailEnabled = request.EmailEnabled ?? true,
            MutedEmailEvents = muted,
            UpdatedAt = now
        };
        db.NotificationPreferences.Add(existing);
    }
    else
    {
        db.Entry(existing).Property(p => p.EmailEnabled).CurrentValue = request.EmailEnabled ?? existing.EmailEnabled;
        db.Entry(existing).Property(p => p.MutedEmailEvents).CurrentValue = muted;
        db.Entry(existing).Property(p => p.UpdatedAt).CurrentValue = now;
    }
    await db.SaveChangesAsync(ct);

    var repo = await db.ConnectedRepositories.AsNoTracking().SingleAsync(r => r.Id == request.RepositoryId, ct);
    var mutedList = Enumerable.Range(0, 8).Where(i => (existing.MutedEmailEvents & (1 << i)) != 0)
        .Select(i => ((WebhookEvent)i).ToString()).ToList();
    return Results.Ok(new NotificationPreferenceResponse(existing.RepositoryId, repo.CloneUrl, existing.EmailEnabled, mutedList, existing.UpdatedAt));
}).RequireAuthorization();

app.MapGet("/api/emails", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct,
    int page = 1, int pageSize = 20, bool? sent = null) =>
{
    var ownerId = user.GetUserId();
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);

    var query = db.EmailMessages.AsNoTracking().Where(m => m.UserId == ownerId);
    if (sent.HasValue)
    {
        query = query.Where(m => m.Sent == sent.Value);
    }

    var total = await query.CountAsync(ct);
    var rows = await query.OrderByDescending(m => m.CreatedAt)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(m => new EmailMessageResponse(m.Id, m.To, m.Subject, m.Sent, m.SentAt, m.Attempts, m.LastError, m.CreatedAt))
        .ToListAsync(ct);

    return Results.Ok(new EmailPage(rows, page, pageSize, total));
}).RequireAuthorization();

app.MapPost("/api/emails/{id:guid}/retry", async (Guid id, ClaimsPrincipal user, DevSupDbContext db, AuditRecorder audit, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var message = await db.EmailMessages.FirstOrDefaultAsync(m => m.Id == id && m.UserId == ownerId, ct);
    if (message is null)
    {
        return Results.NotFound();
    }

    if (message.Sent)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "This email was already sent.");
    }

    db.Entry(message).Property(m => m.Attempts).CurrentValue = 0;
    db.Entry(message).Property(m => m.LastError).CurrentValue = null;
    await db.SaveChangesAsync(ct);

    var actor = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email })
        .SingleAsync(u => u.Id == ownerId, ct);
    await audit.RecordAsync(actor.Id, actor.Email, "email.retry", "EmailMessage",
        message.Id.ToString(), before: message.LastError, ct: ct);

    return Results.Ok(new EmailMessageResponse(message.Id, message.To, message.Subject,
        message.Sent, message.SentAt, message.Attempts, message.LastError, message.CreatedAt));
}).RequireAuthorization();

app.Run();

static async Task<bool> IsAdminAsync(ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
    await db.Users.AsNoTracking().AnyAsync(u => u.Id == user.GetUserId() && u.IsAdmin, ct);

static string CsvEscape(string? value)
{
    if (string.IsNullOrEmpty(value))
    {
        return string.Empty;
    }

    if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    return value;
}

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

static int EventsToMask(IEnumerable<WebhookEvent>? events)
{
    if (events is null)
    {
        return 0;
    }

    var mask = 0;
    foreach (var webhookEvent in events)
    {
        mask |= 1 << (int)webhookEvent;
    }

    return mask;
}

static List<WebhookEvent> ResolveEvents(int mask)
{
    if (mask == 0)
    {
        return Enum.GetValues<WebhookEvent>().ToList();
    }

    return Enum.GetValues<WebhookEvent>()
        .Where(webhookEvent => (mask & (1 << (int)webhookEvent)) != 0)
        .ToList();
}

static string? Truncate(string? value, int maxLength)
    => value is null || value.Length <= maxLength ? value : value[..maxLength];

public partial class Program;