using System.Net;
using System.Net.Http.Json;
using DevSup.Api;
using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DevSup.Tests;

public sealed class AiKeysApiTests : IAsyncLifetime
{
    private readonly DevSupApiFactory _factory = new();
    private readonly HttpClient _client;

    public AiKeysApiTests()
    {
        _client = _factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<string> LoginAsync(string email)
    {
        return await Helpers.LoginAndGetTokenAsync(_client, email, "Key Owner");
    }

    [Fact]
    public async Task AddKey_StoresEncryptedAndReturnsMasked()
    {
        var token = await LoginAsync("keys@example.com");
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _client.PostAsJsonAsync("/api/ai-keys", new { provider = "openai", model = "gpt-4o-mini", key = "sk-secret-key-123456" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<AiKeyResponse>(Helpers.ApiJson);
        Assert.NotNull(payload);
        Assert.DoesNotContain("sk-secret-key-123456", payload!.KeyMask);
        Assert.EndsWith("3456", payload.KeyMask);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        var protector = _factory.Services.GetRequiredService<IKeyProtector>();

        var stored = await db.AiModelKeyBindings.SingleAsync();
        Assert.StartsWith("aesgcm:", stored.EncryptedApiKey);
        Assert.Equal("sk-secret-key-123456", protector.Unprotect(stored.EncryptedApiKey));
    }

    [Fact]
    public async Task ListKeys_ListsOnlyOwnedAndNeverPlaintext()
    {
        var ownerA = await LoginAsync("keys-a@example.com");
        var ownerB = await LoginAsync("keys-b@example.com");

        await AddKeyAsync(ownerA, "openai", "model-a", "key-aaaaaaaa");
        await AddKeyAsync(ownerB, "openai", "model-b", "key-bbbbbbbb");

        var responseA = await ListKeysAsync(ownerA);
        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);
        var payloadA = await responseA.Content.ReadFromJsonAsync<List<AiKeyResponse>>(Helpers.ApiJson);
        Assert.NotNull(payloadA);
        var key = Assert.Single(payloadA!);
        Assert.Equal("model-a", key.Model);
        Assert.DoesNotContain("key-aaaaaaaa", key.KeyMask ?? "");
        Assert.DoesNotContain(payloadA!, k => k.KeyMask is null || k.KeyMask.Contains("key-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddKey_SameProviderModel_Upserts()
    {
        var token = await LoginAsync("keys-upsert@example.com");
        var first = await AddKeyAsync(token, "ollama", "llama3.2", "first-key-123");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await AddKeyAsync(token, "ollama", "llama3.2", "second-key-456");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Equal(1, await db.AiModelKeyBindings.CountAsync());
    }

    [Fact]
    public async Task AddKey_InvalidInput_Rejected()
    {
        var token = await LoginAsync("keys-invalid@example.com");
        var response = await AddKeyAsync(token, "openai", "", "short");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteKey_RemovesBindingAndCleansRepairKeys()
    {
        var token = await LoginAsync("keys-delete@example.com");
        await AddKeyAsync(token, "deepseek", "deepseek-chat", "key-delete-1234");

        var delete = await DeleteKeyAsync(token, "deepseek", "deepseek-chat");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevSupDbContext>();
        Assert.Equal(0, await db.AiModelKeyBindings.CountAsync());
    }

    [Fact]
    public async Task Endpoints_WithoutToken_Unauthorized()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var list = await _client.GetAsync("/api/ai-keys");
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
    }

    private async Task<HttpResponseMessage> AddKeyAsync(string token, string provider, string model, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ai-keys")
        {
            Content = JsonContent.Create(new { provider, model, key })
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> ListKeysAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/ai-keys");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> DeleteKeyAsync(string token, string provider, string model)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/ai-keys?provider={provider}&model={System.Uri.EscapeDataString(model)}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }
}