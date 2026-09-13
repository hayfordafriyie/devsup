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

    [Fact]
    public async Task Dashboard_WebhookFormSupportsRouting()
    {
        using var client = _factory.CreateClient();

        var html = await (await client.GetAsync("/dashboard/")).Content.ReadAsStringAsync();
        Assert.Contains("webhook-channel", html);
        Assert.Contains("option value=\"slack\"", html);
        Assert.Contains("option value=\"teams\"", html);
        Assert.Contains("webhook-name", html);

        var js = await (await client.GetAsync("/dashboard/app.js")).Content.ReadAsStringAsync();
        Assert.Contains("channelBadge", js);
        Assert.Contains("w.channel", js);
        Assert.Contains("channel: channelSelect.value", js);
    }

    [Fact]
    public async Task Dashboard_AdminConsoleHooksPresent()
    {
        using var client = _factory.CreateClient();

        var html = await (await client.GetAsync("/dashboard/")).Content.ReadAsStringAsync();
        Assert.Contains("admin-section", html);
        Assert.Contains("admin-users", html);
        Assert.Contains("admin-audit", html);
        Assert.Contains("admin-failures", html);
        Assert.Contains("id=\"export-csv\"", html);

        var js = await (await client.GetAsync("/dashboard/app.js")).Content.ReadAsStringAsync();
        Assert.Contains("loadAdmin", js);
        Assert.Contains("/api/admin/users", js);
        Assert.Contains("/api/admin/audit", js);
        Assert.Contains("data-suspend", js);
        Assert.Contains("data-restore", js);
        Assert.Contains("exportCsv", js);
        Assert.Contains("/api/failures/export", js);
    }

    [Fact]
    public async Task Dashboard_PreferencesHooksPresent()
    {
        using var client = _factory.CreateClient();

        var html = await (await client.GetAsync("/dashboard/")).Content.ReadAsStringAsync();
        Assert.Contains("preferences-section", html);
        Assert.Contains("preferences-toggle", html);
        Assert.Contains("id=\"preferences\"", html);

        var js = await (await client.GetAsync("/dashboard/app.js")).Content.ReadAsStringAsync();
        Assert.Contains("loadPreferences", js);
        Assert.Contains("/api/notification-preferences", js);
        Assert.Contains("data-event", js);
        Assert.Contains("savePreference", js);
    }

    [Fact]
    public async Task Dashboard_DeliveryCenterHooksPresent()
    {
        using var client = _factory.CreateClient();

        var html = await (await client.GetAsync("/dashboard/")).Content.ReadAsStringAsync();
        Assert.Contains("delivery-section", html);
        Assert.Contains("id=\"emails\"", html);

        var js = await (await client.GetAsync("/dashboard/app.js")).Content.ReadAsStringAsync();
        Assert.Contains("loadEmails", js);
        Assert.Contains("/api/emails?pageSize=50", js);
        Assert.Contains("renderEmails", js);
        Assert.Contains("data-retry-email", js);
        Assert.Contains("\"/api/emails/\" + emailId", js);
        Assert.Contains("data-ping", js);
        Assert.Contains("\"/api/webhooks/\" + pingId", js);
        Assert.Contains("/test", js);
        Assert.Contains("data-log", js);
        Assert.Contains("\"/api/webhooks/\" + webhookId + \"/deliveries?pageSize=20\"", js);
        Assert.Contains("data-retry-delivery", js);
        Assert.Contains("\"/deliveries/\" + deliveryId + \"/retry\"", js);
        Assert.Contains("renderDeliveryLog", js);
        var html2 = await (await client.GetAsync("/dashboard/")).Content.ReadAsStringAsync();
        Assert.Contains("ticket-detail-section", html2);
        Assert.Contains("\"/api/tickets/\" + ticketId", js);
        Assert.Contains("data-ticket-detail", js);
        Assert.Contains("data-ticket-close", js);
        Assert.Contains("data-ticket-reopen", js);
        Assert.Contains("account-section", html2);
        Assert.Contains("digest-toggle", js);
        Assert.Contains("digestEnabled", js);
        Assert.Contains("data-repo-pause", js);
        Assert.Contains("data-repo-resume", js);
        Assert.Contains("\"/api/repositories/\" + pauseId + \"/pause\"", js);
        Assert.Contains("\"/api/repositories/\" + resumeId + \"/unpause\"", js);
        Assert.Contains("data-webhook-pause", js);
        Assert.Contains("data-webhook-resume", js);
        Assert.Contains("\"/api/webhooks/\" + pauseHookId + \"/deactivate\"", js);
        Assert.Contains("\"/api/webhooks/\" + resumeHookId + \"/activate\"", js);
        Assert.Contains("repo-members-panel", html2);
        Assert.Contains("data-repo-members", js);
        Assert.Contains("loadRepositoryMembers", js);
        Assert.Contains("data-member-remove", js);
        Assert.Contains("data-member-repo", js);
        Assert.Contains("\"/api/repositories/\" + repoId + \"/members\"", js);
        Assert.Contains("data-members-close", js);
        Assert.Contains("repo-member-form", html2);
        Assert.Contains("canTriage", js);
    }
}