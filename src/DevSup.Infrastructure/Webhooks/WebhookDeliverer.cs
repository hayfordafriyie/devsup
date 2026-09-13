using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DevSup.Core;
using Microsoft.Extensions.Logging;

namespace DevSup.Infrastructure.Webhooks;

/// <summary>
/// Attempts a single HTTP POST of a webhook payload. Retries and status tracking are
/// owned by the outbox processor; this type only throws on transport or non-2xx failure.
/// </summary>
public interface IWebhookDeliverer
{
    Task DeliverAsync(string url, string secret, string payload, WebhookEvent webhookEvent, WebhookChannel channel, CancellationToken ct);
}

public sealed class HttpWebhookDeliverer(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpWebhookDeliverer> logger) : IWebhookDeliverer
{
    public async Task DeliverAsync(string url, string secret, string payload, WebhookEvent webhookEvent, WebhookChannel channel, CancellationToken ct)
    {
        var body = WebhookPayloadFormatter.Format(channel, payload);

        using var client = httpClientFactory.CreateClient("webhooks");
        client.Timeout = TimeSpan.FromSeconds(10);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(WebhookQueue.EventHeader, CamelName(webhookEvent));
        request.Headers.TryAddWithoutValidation(WebhookQueue.SignatureHeader, "sha256=" + ComputeSignature(secret, body));

        using var response = await client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            logger.LogDebug("Delivered webhook event {Event} to {Url} via {Channel}", webhookEvent, url, channel);
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>sha256 HMAC over the raw payload bytes using the endpoint secret.</summary>
    public static string ComputeSignature(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>WebhookEvent names are single words, so camelCase means lowering the first letter.</summary>
    private static string CamelName(WebhookEvent webhookEvent)
    {
        var name = webhookEvent.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}