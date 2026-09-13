using System.Text.Json.Nodes;
using DevSup.Core;

namespace DevSup.Infrastructure.Webhooks;

/// <summary>
/// Translates the base DevSup event payload into a channel-appropriate wire body.
/// <see cref="WebhookChannel.Http"/> returns the payload byte-for-byte; Slack and Teams
/// get compact human-readable messages. The HMAC signature is always computed over the
/// body that is actually sent.
/// </summary>
public static class WebhookPayloadFormatter
{
    public const string TeamsTheme = "E81123";

    /// <summary>Returns the body to POST for the given channel; never throws on malformed JSON.</summary>
    public static string Format(WebhookChannel channel, string payload)
    {
        if (channel == WebhookChannel.Http)
        {
            return payload;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch
        {
            return payload;
        }

        var eventName = Read(root, "event") ?? $"ticket-{Read(root, "ticket", "status")}";
        var method = Read(root, "failure", "method") ?? "?";
        var path = Read(root, "failure", "path") ?? "?";
        var code = Read(root, "failure", "statusCode") ?? "?";
        var repository = Read(root, "repository", "cloneUrl") ?? "?";
        var ticketId = Read(root, "ticket", "id") ?? "?";

        return channel == WebhookChannel.Slack
            ? BuildSlack(eventName, method, path, code, repository, ticketId)
            : BuildTeams(eventName, method, path, code, repository, ticketId);
    }

    private static string BuildSlack(string eventName, string method, string path, string code, string repository, string ticketId)
    {
        return new JsonObject
        {
            ["text"] = $"DevSup {eventName}: `{method} {path}` (HTTP {code})",
            ["blocks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "section",
                    ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*DevSup {eventName}*" }
                },
                new JsonObject
                {
                    ["type"] = "section",
                    ["fields"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*Failure:*\n`{method} {path}`" },
                        new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*Status:*\nHTTP {code}" },
                        new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*Repository:*\n<{repository}|{repository}>" },
                        new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*Ticket:*\n`{ticketId}`" }
                    }
                }
            }
        }.ToJsonString();
    }

    private static string BuildTeams(string eventName, string method, string path, string code, string repository, string ticketId)
    {
        return new JsonObject
        {
            ["@type"] = "MessageCard",
            ["@context"] = "http://schema.org/extensions",
            ["themeColor"] = TeamsTheme,
            ["summary"] = $"DevSup {eventName}",
            ["sections"] = new JsonArray
            {
                new JsonObject
                {
                    ["activityTitle"] = $"DevSup {eventName}",
                    ["facts"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "Failure", ["value"] = $"{method} {path}" },
                        new JsonObject { ["name"] = "HTTP status", ["value"] = code },
                        new JsonObject { ["name"] = "Repository", ["value"] = repository },
                        new JsonObject { ["name"] = "Ticket", ["value"] = ticketId }
                    }
                }
            }
        }.ToJsonString();
    }

    private static string? Read(JsonNode? root, params string[] path)
    {
        if (root is null)
        {
            return null;
        }

        JsonNode? current = root;
        foreach (var segment in path)
        {
            current = current is JsonObject obj ? obj[segment] : null;
            if (current is null)
            {
                return null;
            }
        }

        return current.GetValueKind() == System.Text.Json.JsonValueKind.String ? current.GetValue<string>() : current.ToJsonString();
    }
}