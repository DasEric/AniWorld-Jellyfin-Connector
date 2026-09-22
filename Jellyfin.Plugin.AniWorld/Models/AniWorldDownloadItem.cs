using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AniWorld.Models;

/// <summary>
/// One entry accepted by AniWorld's download queue. Normal video episodes are
/// serialized as URL strings; sources such as MangaFire use the extended
/// object form supported by the downloader.
/// </summary>
[JsonConverter(typeof(AniWorldDownloadItemJsonConverter))]
public sealed class AniWorldDownloadItem
{
    public AniWorldDownloadItem()
    {
    }

    public AniWorldDownloadItem(string url)
    {
        Url = url;
    }

    public string Url { get; set; } = string.Empty;

    public string? SeriesUrl { get; set; }

    public IReadOnlyList<int>? SelectedPages { get; set; }

    public string? MangaFireFormat { get; set; }
}

public sealed class AniWorldDownloadItemJsonConverter : JsonConverter<AniWorldDownloadItem>
{
    public override AniWorldDownloadItem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new AniWorldDownloadItem(reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("AniWorld download entries must be URL strings or objects.");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var item = new AniWorldDownloadItem
        {
            Url = ReadString(root, "url"),
            SeriesUrl = ReadOptionalString(root, "series_url"),
            MangaFireFormat = ReadOptionalString(root, "mangafire_format"),
        };

        if (root.TryGetProperty("selected_pages", out var pages))
        {
            if (pages.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("selected_pages must be an array.");
            }

            item.SelectedPages = pages.EnumerateArray()
                .Select(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                    ? number
                    : throw new JsonException("selected_pages contains an invalid page number."))
                .ToArray();
        }

        return item;
    }

    public override void Write(Utf8JsonWriter writer, AniWorldDownloadItem value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value.SeriesUrl)
            && value.SelectedPages is null
            && string.IsNullOrWhiteSpace(value.MangaFireFormat))
        {
            writer.WriteStringValue(value.Url);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("url", value.Url);
        if (!string.IsNullOrWhiteSpace(value.SeriesUrl))
        {
            writer.WriteString("series_url", value.SeriesUrl);
        }

        if (value.SelectedPages is not null)
        {
            writer.WritePropertyName("selected_pages");
            writer.WriteStartArray();
            foreach (var page in value.SelectedPages)
            {
                writer.WriteNumberValue(page);
            }

            writer.WriteEndArray();
        }

        if (!string.IsNullOrWhiteSpace(value.MangaFireFormat))
        {
            writer.WriteString("mangafire_format", value.MangaFireFormat);
        }

        writer.WriteEndObject();
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new JsonException($"Missing string property '{name}'.");

    private static string? ReadOptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
