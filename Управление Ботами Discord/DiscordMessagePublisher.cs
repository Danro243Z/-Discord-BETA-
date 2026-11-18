using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Управление_Ботами_Discord;

internal sealed record DiscordEmbed(string Description, int Color);

internal sealed record ParsedDiscordContent(string PlainText, IReadOnlyList<DiscordEmbed> Embeds);

internal static class DiscordFileParser
{
    private const int DefaultEmbedColor = 0x5865F2;
    private static readonly Regex SeparatorPattern = new(@"-{10,}\s*(#[0-9a-fA-F]{3,6})?", RegexOptions.Compiled);

    public static ParsedDiscordContent Parse(string content, string firstEmbedColor)
    {
        if (!TryParseHexColor(firstEmbedColor, out int firstColor))
        {
            throw new ArgumentException("Укажите цвет в формате #RGB или #RRGGBB.", nameof(firstEmbedColor));
        }

        MatchCollection separators = SeparatorPattern.Matches(content);
        if (separators.Count == 0)
        {
            return new ParsedDiscordContent(content.Trim(), Array.Empty<DiscordEmbed>());
        }

        string plainText = content[..separators[0].Index].Trim();
        var embeds = new List<DiscordEmbed>();

        for (int i = 0; i < separators.Count; i++)
        {
            Match separator = separators[i];
            int start = separator.Index + separator.Length;
            int end = i + 1 < separators.Count ? separators[i + 1].Index : content.Length;
            string description = content[start..end].Trim();
            if (description.Length == 0)
            {
                continue;
            }

            int color = embeds.Count == 0
                ? firstColor
                : TryParseHexColor(separator.Groups[1].Value, out int parsedColor) ? parsedColor : DefaultEmbedColor;

            foreach (string chunk in SplitText(description, 4096))
            {
                embeds.Add(new DiscordEmbed(chunk, color));
            }
        }

        return new ParsedDiscordContent(plainText, embeds);
    }

    private static bool TryParseHexColor(string? color, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(color))
        {
            return false;
        }

        string hex = color.Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(character => new string(character, 2)));
        }
        else if (hex.Length != 6)
        {
            return false;
        }

        return int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    internal static IReadOnlyList<string> SplitText(string text, int maxLength)
    {
        var chunks = new List<string>();
        for (int start = 0; start < text.Length;)
        {
            int end = Math.Min(start + maxLength, text.Length);
            if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
            {
                end--;
            }

            chunks.Add(text[start..end]);
            start = end;
        }

        return chunks;
    }
}

internal static class DiscordMessagePublisher
{
    private const int MaxMessageLength = 2000;
    private const int MaxEmbedCount = 10;
    private const int MaxEmbedTextLength = 6000;
    private static readonly HttpClient HttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> PublishAsync(string token, ulong channelId, ParsedDiscordContent content)
    {
        int sentCount = 0;
        foreach (string chunk in DiscordFileParser.SplitText(content.PlainText, MaxMessageLength))
        {
            await SendAsync(token, channelId, new MessagePayload(chunk, null));
            sentCount++;
        }

        foreach (IReadOnlyList<DiscordEmbed> group in GroupEmbeds(content.Embeds))
        {
            var embeds = group.Select(embed => new EmbedPayload(embed.Description, embed.Color)).ToArray();
            await SendAsync(token, channelId, new MessagePayload(null, embeds));
            sentCount++;
        }

        return sentCount;
    }

    public static Task EditAsync(string token, ulong channelId, ulong messageId, ParsedDiscordContent content)
    {
        if (content.PlainText.Length > MaxMessageLength)
        {
            throw new InvalidOperationException("Для редактирования текстовая часть должна содержать не более 2000 символов.");
        }

        if (content.Embeds.Count > MaxEmbedCount || content.Embeds.Sum(embed => embed.Description.Length) > MaxEmbedTextLength)
        {
            throw new InvalidOperationException("Для редактирования Discord допускает не более 10 embed-объектов и 6000 символов в их описаниях суммарно.");
        }

        var embeds = content.Embeds.Select(embed => new EmbedPayload(embed.Description, embed.Color)).ToArray();
        var payload = new MessagePayload(
            content.PlainText.Length == 0 ? null : content.PlainText,
            embeds);

        return SendAsync(token, channelId, payload, messageId);
    }

    private static IEnumerable<IReadOnlyList<DiscordEmbed>> GroupEmbeds(IReadOnlyList<DiscordEmbed> embeds)
    {
        var group = new List<DiscordEmbed>();
        int groupTextLength = 0;

        foreach (DiscordEmbed embed in embeds)
        {
            if (group.Count == MaxEmbedCount || groupTextLength + embed.Description.Length > MaxEmbedTextLength)
            {
                yield return group;
                group = new List<DiscordEmbed>();
                groupTextLength = 0;
            }

            group.Add(embed);
            groupTextLength += embed.Description.Length;
        }

        if (group.Count > 0)
        {
            yield return group;
        }
    }

    private static async Task SendAsync(string token, ulong channelId, MessagePayload payload, ulong? messageId = null)
    {
        string endpoint = $"https://discord.com/api/v10/channels/{channelId}/messages";
        if (messageId.HasValue)
        {
            endpoint += $"/{messageId.Value}";
        }

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(messageId.HasValue ? HttpMethod.Patch : HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);

            using HttpResponseMessage response = await HttpClient.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            if ((int)response.StatusCode == 429 && attempt < 3)
            {
                await Task.Delay(GetRetryDelay(response, responseBody));
                continue;
            }

            string detail = GetDiscordError(responseBody) ?? response.ReasonPhrase ?? "Неизвестная ошибка.";
            throw new HttpRequestException($"Discord вернул ошибку {(int)response.StatusCode}: {detail}", null, response.StatusCode);
        }
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, string responseBody)
    {
        TimeSpan? headerDelay = response.Headers.RetryAfter?.Delta;
        if (headerDelay is null && response.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
        {
            headerDelay = retryDate - DateTimeOffset.UtcNow;
        }

        if (headerDelay.HasValue)
        {
            return headerDelay.Value > TimeSpan.Zero ? headerDelay.Value : TimeSpan.Zero;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("retry_after", out JsonElement retryAfter) && retryAfter.TryGetDouble(out double seconds))
            {
                return TimeSpan.FromSeconds(Math.Max(0, seconds));
            }
        }
        catch (JsonException)
        {
        }

        return TimeSpan.FromSeconds(1);
    }

    private static string? GetDiscordError(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return responseBody;
    }

    private sealed record MessagePayload(string? Content, IReadOnlyList<EmbedPayload>? Embeds);
    private sealed record EmbedPayload(string Description, int Color);
}
