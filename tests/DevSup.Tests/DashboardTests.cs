using System.Net;
using Xunit;

namespace DevSup.Tests;

public sealed class DashboardTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Dashboard_ServesIndexHtml()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/dashboard/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType!.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("DevSup Dashboard", body);
        Assert.Contains("/dashboard/app.js", body);
    }

    [Fact]
    public async Task Dashboard_ServesAssets()
    {
        using var client = _factory.CreateClient();
        var js = await client.GetAsync("/dashboard/app.js");
        var css = await client.GetAsync("/dashboard/style.css");

        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Contains("javascript", js.Content.Headers.ContentType!.ToString());
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Contains("css", css.Content.Headers.ContentType!.ToString());
    }
}