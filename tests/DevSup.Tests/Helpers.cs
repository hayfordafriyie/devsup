using System.Net;
using System.Net.Http.Json;
using DevSup.Api;
using System.Text;
using System.Text.Json;

namespace DevSup.Tests;

public static class Helpers
{
    public static async Task<string> LoginAndGetTokenAsync(HttpClient client, string email, string displayName)
    {
        await client.PostAsJsonAsync("/api/users/register", new { email, displayName, password = "password123" });

        var login = await client.PostAsJsonAsync("/api/users/login", new { email, password = "password123" });
        Assert.NotNull(login);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var payload = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(payload);
        return payload.Token;
    }

    public static async Task<Guid> CreateRepositoryAsync(HttpClient client, string token, string cloneUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/repositories")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { provider = "github", cloneUrl, defaultBranch = "main" }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var repository = await response.Content.ReadFromJsonAsync<RepositoryResponse>();
        Assert.NotNull(repository);
        return repository.Id;
    }

    public static Task<HttpResponseMessage> IngestAsync(
        HttpClient client, string token, Guid repositoryId, int statusCode, string method, string path, string? exceptionMessage = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { repositoryId, statusCode, method, path, exceptionMessage }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        return client.SendAsync(request);
    }

    public static string? GetQueryParam(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == name)
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }
}