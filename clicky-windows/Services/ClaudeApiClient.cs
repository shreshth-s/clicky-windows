using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ClickyWindows.Services;

/// <summary>
/// Sends vision requests to Claude via the Cloudflare Worker proxy and streams
/// the response back via SSE.
/// </summary>
public sealed class ClaudeApiClient
{
    private readonly string _chatProxyUrl;
    private readonly HttpClient _httpClient;

    public string Model { get; set; }

    public ClaudeApiClient(string workerBaseUrl, string model = "claude-sonnet-4-6")
    {
        _chatProxyUrl = $"{workerBaseUrl}/chat";
        Model = model;

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _ = WarmUpTlsConnectionAsync(workerBaseUrl);
    }

    public async Task<string> AnalyzeScreensAndAskAsync(
        IReadOnlyList<(byte[] JpegData, string Label, int Width, int Height)> screenshots,
        string systemPrompt,
        IReadOnlyList<(string UserMessage, string AssistantResponse)> conversationHistory,
        string userPrompt,
        Action<string> onTextChunk,
        CancellationToken cancellationToken = default
    )
    {
        var requestBody = BuildRequestBody(screenshots, systemPrompt, conversationHistory, userPrompt);
        string requestJson = JsonSerializer.Serialize(requestBody);

        double payloadSizeMb = Encoding.UTF8.GetByteCount(requestJson) / 1_048_576.0;
        Console.WriteLine(
            $"🌐 Claude streaming request: {payloadSizeMb:F1}MB, {screenshots.Count} screenshot(s)"
        );

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, _chatProxyUrl)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Claude API error ({(int)response.StatusCode}): {errorBody}"
            );
        }

        var accumulatedResponseText = new StringBuilder();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var streamReader = new System.IO.StreamReader(responseStream);

        while (!streamReader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            string? line = await streamReader.ReadLineAsync(cancellationToken);
            if (line == null) break;

            if (!line.StartsWith("data: ")) continue;

            string jsonPayload = line["data: ".Length..];
            if (jsonPayload == "[DONE]") break;

            string? textChunk = ExtractTextDeltaFromSseEvent(jsonPayload);
            if (textChunk == null) continue;

            accumulatedResponseText.Append(textChunk);
            onTextChunk(accumulatedResponseText.ToString());
        }

        string fullResponse = accumulatedResponseText.ToString();
        Console.WriteLine(
            $"🌐 Claude response complete: {fullResponse.Length} chars"
        );

        return fullResponse;
    }

    private object BuildRequestBody(
        IReadOnlyList<(byte[] JpegData, string Label, int Width, int Height)> screenshots,
        string systemPrompt,
        IReadOnlyList<(string UserMessage, string AssistantResponse)> conversationHistory,
        string userPrompt
    )
    {
        var messages = new List<object>();

        foreach (var (userMessage, assistantResponse) in conversationHistory)
        {
            messages.Add(new { role = "user", content = userMessage });
            messages.Add(new { role = "assistant", content = assistantResponse });
        }

        var contentBlocks = new List<object>();

        foreach (var (jpegData, label, _, _) in screenshots)
        {
            contentBlocks.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = "image/jpeg",
                    data = Convert.ToBase64String(jpegData)
                }
            });

            contentBlocks.Add(new { type = "text", text = label });
        }

        contentBlocks.Add(new { type = "text", text = userPrompt });
        messages.Add(new { role = "user", content = contentBlocks });

        return new
        {
            model = Model,
            max_tokens = 1024,
            stream = true,
            system = systemPrompt,
            messages
        };
    }

    private static string? ExtractTextDeltaFromSseEvent(string jsonPayload)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement)) return null;
            if (typeElement.GetString() != "content_block_delta") return null;

            if (!root.TryGetProperty("delta", out var deltaElement)) return null;
            if (!deltaElement.TryGetProperty("type", out var deltaTypeElement)) return null;
            if (deltaTypeElement.GetString() != "text_delta") return null;

            if (!deltaElement.TryGetProperty("text", out var textElement)) return null;
            return textElement.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WarmUpTlsConnectionAsync(string workerBaseUrl)
    {
        try
        {
            var uri = new Uri(workerBaseUrl);
            var warmupUrl = $"{uri.Scheme}://{uri.Host}/";
            await _httpClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, warmupUrl),
                HttpCompletionOption.ResponseHeadersRead
            );
            Console.WriteLine("🌐 TLS warmup complete");
        }
        catch
        {
            // Intentionally ignored
        }
    }
}
