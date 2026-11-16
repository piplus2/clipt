using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Clipt.Models;

namespace Clipt.Services;

public sealed record AiAction(string Name, string Glyph, string Instruction, bool ForText = true, bool ForImages = false)
{
    public bool IsCustom { get; init; }
}

/// <summary>Optional AI transforms over clips. Off unless the user enables it in Settings.</summary>
public sealed class AiService
{
    private const string SystemPrompt =
        "You are the assistant inside Clipt, a clipboard manager for Windows. The user selected an item from their " +
        "clipboard history (given inside <clip> tags, or attached as an image) and asked you to transform or analyse it. " +
        "Reply with only the result, ready to paste: no preamble, no closing remarks, no surrounding quotes. " +
        "Preserve the language of the original unless asked to translate. Use Markdown only when the request calls for structure " +
        "such as lists, or when the clip already uses it.";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly Func<AppSettings> _settings;

    public AiService(Func<AppSettings> settings) => _settings = settings;

    public bool Enabled => _settings().AiEnabled;

    public string ProviderLabel => _settings().AiProvider == "Ollama"
        ? $"Ollama · {_settings().OllamaModel}"
        : $"Claude · {_settings().AnthropicModel}";

    public IReadOnlyList<AiAction> ActionsFor(ClipItem item)
    {
        var lang = _settings().TranslateTo;
        var all = new List<AiAction>
        {
            new("Summarize", "", "Summarize this concisely."),
            new("Improve writing", "", "Rewrite this to be clearer and more polished while keeping its meaning and tone."),
            new("Fix spelling & grammar", "", "Fix spelling, grammar and punctuation. Change as little as possible."),
            new("Make shorter", "", "Make this noticeably shorter while keeping the key points."),
            new("Make it professional", "", "Rewrite this in a clear, professional tone."),
            new("Make it friendly", "", "Rewrite this in a warm, friendly, casual tone."),
            new("Bullet points", "", "Turn this into a concise bulleted list."),
            new($"Translate to {lang}", "", $"Translate this into {lang}. If it is already in {lang}, translate it into English."),
            new("Explain", "", "Explain what this is and what it means, briefly and clearly. If it is code, explain what it does."),
            new("Reply to this", "", "Draft a short, helpful reply to this message."),
            new("Describe image", "", "Describe this image in detail.", ForText: false, ForImages: true),
            new("Extract text", "", "Transcribe all text visible in this image exactly, preserving line breaks.", ForText: false, ForImages: true),
            new("Image to alt text", "", "Write concise, useful alt text for this image (one sentence).", ForText: false, ForImages: true),
        };
        bool image = item.Kind == ClipKind.Image;
        return all.Where(a => image ? a.ForImages : a.ForText).ToList();
    }

    public async IAsyncEnumerable<string> RunAsync(ClipItem item, string instruction, [EnumeratorCancellation] CancellationToken ct)
    {
        // Hard guarantee, independent of the UI: secrets never leave the machine.
        if (item.Sensitive)
            throw new InvalidOperationException("AI is turned off for sensitive clips — they never leave your PC.");

        byte[]? png = null;
        string userText;
        if (item.Kind == ClipKind.Image)
        {
            png = ClipItem.ImageReader?.Invoke(item.ImagePath)
                  ?? throw new InvalidOperationException("The image for this clip is no longer available.");
            userText = instruction;
        }
        else
        {
            var text = item.Kind == ClipKind.Files ? string.Join("\n", item.Paths) : item.Text ?? "";
            userText = $"<clip>\n{text}\n</clip>\n\n{instruction}";
        }

        var s = _settings();
        var stream = s.AiProvider == "Ollama"
            ? StreamOllama(s, userText, png, ct)
            : StreamClaude(s, userText, png, ct);
        await foreach (var chunk in stream.WithCancellation(ct))
            yield return chunk;
    }

    /// <summary>Round-trips a tiny request with not-yet-saved settings.</summary>
    public static async Task<string> TestAsync(AppSettings s, CancellationToken ct)
    {
        const string prompt = "<clip>\nping\n</clip>\n\nReply with exactly the word: pong";
        var stream = s.AiProvider == "Ollama" ? StreamOllama(s, prompt, null, ct) : StreamClaude(s, prompt, null, ct);
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in stream.WithCancellation(ct)) sb.Append(chunk);
        return sb.Length == 0 ? "(empty reply)" : sb.ToString();
    }

    // ---------- Claude (official Anthropic SDK) ----------

    private static async IAsyncEnumerable<string> StreamClaude(AppSettings s, string userText, byte[]? png,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var key = s.AnthropicKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Add your Anthropic API key in Settings → AI (or set ANTHROPIC_API_KEY).");

        var client = new AnthropicClient { ApiKey = key };

        var content = new List<ContentBlockParam>();
        if (png is not null)
        {
            content.Add(new ImageBlockParam
            {
                Source = new Base64ImageSource { Data = Convert.ToBase64String(png), MediaType = MediaType.ImagePng },
            });
        }
        content.Add(new TextBlockParam { Text = userText });

        var parameters = new MessageCreateParams
        {
            Model = s.AnthropicModel,
            MaxTokens = 16000,
            System = new List<TextBlockParam> { new() { Text = SystemPrompt } },
            Messages = [new UserMessageParam { Content = content }],
            // Clipboard transforms are short, interactive tasks: favour latency.
            OutputConfig = SupportsEffort(s.AnthropicModel) ? new OutputConfig { Effort = Effort.Low } : null,
        };

        await foreach (var ev in client.Messages.CreateStreaming(parameters, ct))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                yield return text.Text;
            else if (ev.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason == StopReason.Refusal)
                yield return "\n\n[Claude declined this request.]";
        }
    }

    private static bool SupportsEffort(string model) =>
        !(model.Contains("haiku-4") || model.Contains("sonnet-4-5") || model.Contains("-3-"));

    // ---------- Ollama (local models) ----------

    private static async IAsyncEnumerable<string> StreamOllama(AppSettings s, string userText, byte[]? png,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Clips may be private: never send them over the network unencrypted.
        if (!Uri.TryCreate(s.OllamaEndpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback))
            throw new InvalidOperationException("For a remote Ollama server use an https:// address — plain http would send your clips unencrypted.");

        var user = new Dictionary<string, object> { ["role"] = "user", ["content"] = userText };
        if (png is not null) user["images"] = new[] { Convert.ToBase64String(png) };
        var body = new
        {
            model = s.OllamaModel,
            stream = true,
            messages = new object[] { new { role = "system", content = SystemPrompt }, user },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, s.OllamaEndpoint.TrimEnd('/') + "/api/chat")
        {
            Content = JsonContent.Create(body),
        };
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");

        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("error", out var err))
                throw new InvalidOperationException(err.GetString());
            if (doc.RootElement.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                yield return c.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("done", out var done) && done.GetBoolean()) yield break;
        }
    }
}
