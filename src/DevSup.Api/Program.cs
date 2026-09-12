using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DevSup.Api;
using DevSup.Api.Auth;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Core.Services;
using DevSup.Infrastructure.Persistence;
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
        new RepositoryResponse(repository.Id, repository.Provider, repository.CloneUrl, repository.DefaultBranch, repository.AppUrl));
});

app.MapGet("/api/repositories", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();
    var repositories = await db.ConnectedRepositories
        .AsNoTracking()
        .Where(r => r.OwnerUserId == ownerId)
        .Select(r => new RepositoryResponse(r.Id, r.Provider, r.CloneUrl, r.DefaultBranch, r.AppUrl))
        .ToListAsync(ct);

    return Results.Ok(repositories);
});

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
        db.EmailMessages.Add(new EmailMessage
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            To = owner.Email,
            Subject = $"DevSup: failure detected on {failure.Method} {failure.Path}",
            HtmlBody = $"<p>DevSup detected a failure on <code>{failure.Method} {failure.Path}</code> " +
                       $"with status <strong>{failure.StatusCode}</strong>.</p>" +
                       $"<p>Classification: <strong>{kind}</strong> ({category}).</p>" +
                       $"<p>Next step: ticket <strong>{ticket.Status}</strong> — the agent will investigate code errors.</p>",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/tickets/{ticket.Id}",
        new IngestResponse(failure.Id, ticket.Id, category.ToString(), kind.ToString(), status.ToString()));
});

app.MapGet("/api/tickets", async (ClaimsPrincipal user, DevSupDbContext db, CancellationToken ct) =>
{
    var ownerId = user.GetUserId();

    var tickets = await db.RepairTickets
        .AsNoTracking()
        .Where(t => db.ConnectedRepositories.Any(r => r.Id == t.RepositoryId && r.OwnerUserId == ownerId))
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
        .ToListAsync(ct);

    return Results.Ok(tickets);
});

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