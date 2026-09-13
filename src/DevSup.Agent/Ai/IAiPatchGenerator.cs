namespace DevSup.Agent.Ai;

using DevSup.Core;
using DevSup.Core.Models;

/// <summary>A single file replacement suggested by a model for a failure.</summary>
public sealed record AiPatchSuggestion(
    string File,
    string Fragment,
    string Replacement,
    string? Summary);

public sealed record AiGenerationRequest(
    FailureEvent Failure,
    ErrorKind Kind,
    string Prompt);

/// <summary>
/// One-shot, credential-taking patch generator. Implementations decide how to talk
/// to their model (OpenAI-compatible chat, Anthropic Messages, Gemini generateContent);
/// returning null means "no usable patch" and the pipeline falls back to templates.
/// </summary>
public interface IAiPatchGenerator
{
    Task<AiPatchSuggestion?> GenerateAsync(AiGenerationRequest request, AiModelEndpoint endpoint, string apiKey, CancellationToken ct);
}

/// <summary>Hard-coded prompt asking the model for a surgical patch as JSON.</summary>
public static class AiPrompts
{
    public const string RepairPrompt = """
        You are DevSup, an expert software engineer repairing a production API failure.
        Given the failure below, produce a SINGLE minimal, surgical code repair as JSON.
        {codeContext}

        Rules:
        - "file" is the repository-relative path of the file to change.
        - "fragment" is an EXACT contiguous string that exists verbatim in the current file.
        - "replacement" is the new text to substitute for that one occurrence.
        - The replacement must compile on its own (full statements, balanced braces).
        - Prefer a null-guard, early-return, or timeout/retry wrap around the failing line.
        - Never invent files; only edit a file you were shown or is obvious from the stack trace.
        - "summary" is a short commit-message style sentence.

        Respond with ONLY this JSON, no markdown fences:
        {"file": "...", "fragment": "...", "replacement": "...", "summary": "..."}
        """;
}