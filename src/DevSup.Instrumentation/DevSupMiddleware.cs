using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DevSup.Instrumentation;

/// <summary>
/// Captures API failures (exceptions and failure status codes) and post-processes
/// them asynchronously to the DevSup ingest endpoint. A place-holder pipeline that
/// is extended as part of the project roadmap.
/// </summary>
public sealed class DevSupMiddleware(
    RequestDelegate next,
    DevSupInstrumentationOptions options,
    IHttpClientFactory httpClientFactory)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        var requestStart = DateTimeOffset.UtcNow;

        try
        {
            await next(context);

            if (IsReportableStatus(context.Response.StatusCode))
            {
                await Capture(context, requestStart, exception: null);
            }
        }
        catch (Exception ex)
        {
            await Capture(context, requestStart, ex);
            throw;
        }
    }

    private async Task Capture(HttpContext context, DateTimeOffset startedAt, Exception? exception)
    {
        var statusCode = exception is null ? context.Response.StatusCode : 500;
        var path = context.Request.Path.ToString();
        var method = context.Request.Method;

        var payload = DevSupPayloadSanitizer.Redact(await ReadBodyAsync(context.Request));
        var eventPayload = new
        {
            repositoryId = options.RepositoryId,
            schemaVersion = options.SchemaVersion,
            sdkVersion = DevSupInstrumentationDefaults.SdkVersion,
            statusCode,
            method,
            path,
            requestPayload = payload,
            exceptionMessage = exception?.Message,
            stackTrace = exception?.StackTrace,
            occurredAt = startedAt
        };

        using var httpClient = httpClientFactory.CreateClient("devsup");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {options.ApiToken}");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            DevSupInstrumentationDefaults.SchemaVersionHeaderName,
            options.SchemaVersion.ToString(CultureInfo.InvariantCulture));

        try
        {
            // Report-and-forget: failures here must never break the application.
            await httpClient.PostAsJsonAsync(options.IngestEndpoint, eventPayload);
        }
        catch
        {
            // Swallowed deliberately.
        }
    }

    private async Task<string?> ReadBodyAsync(HttpRequest request)
    {
        if (!request.Body.CanRead)
        {
            return null;
        }

        request.Body.Position = 0;
        using var reader = new StreamReader(
            request.Body,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);

        var text = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        if (text.Length > options.MaxCapturedPayloadLength)
        {
            return text[..options.MaxCapturedPayloadLength];
        }

        return text;
    }

    private bool IsReportableStatus(int statusCode)
        => Array.IndexOf(options.FailureStatusCodes, statusCode) >= 0;
}

public static class DevSupMiddlewareExtensions
{
    public static IApplicationBuilder UseDevSup(this IApplicationBuilder builder, DevSupInstrumentationOptions options)
        => builder.UseMiddleware<DevSupMiddleware>(options);
}