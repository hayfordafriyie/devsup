using Microsoft.Extensions.Logging;

namespace DevSup.Infrastructure.HealthChecks;

public sealed record AppProbeResult(bool Healthy, int StatusCode, string? Error);

/// <summary>
/// Performs a single liveness GET against a configured app URL. Transport failures
/// are reported as unhealthy (status 0) so the caller still records a failure.
/// </summary>
public interface IAppUrlProber
{
    Task<AppProbeResult> ProbeAsync(string url, TimeSpan timeout, CancellationToken ct);
}

public sealed class HttpAppUrlProber(IHttpClientFactory httpClientFactory) : IAppUrlProber
{
    public async Task<AppProbeResult> ProbeAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient("health");
        client.Timeout = timeout;

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return new AppProbeResult(response.IsSuccessStatusCode, (int)response.StatusCode,
                response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException)
        {
            return new AppProbeResult(false, 0, $"Request to {url} timed out.");
        }
        catch (Exception ex)
        {
            return new AppProbeResult(false, 0, $"Probe failed: {ex.Message}");
        }
    }
}

public sealed class HealthCheckOptions
{
    public bool Enabled { get; init; } = true;
    public int IntervalSeconds { get; init; } = 300;
    public int TimeoutSeconds { get; init; } = 10;
    public int BatchSize { get; init; } = 20;
}