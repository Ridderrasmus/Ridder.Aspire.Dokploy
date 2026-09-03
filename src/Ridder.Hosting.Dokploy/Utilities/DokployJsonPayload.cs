using System.Text.Json;

namespace Ridder.Hosting.Dokploy.Utilities;

internal static class DokployJsonPayload
{
    internal static string Normalize(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return payload;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                var inner = doc.RootElement.GetString();
                if (!string.IsNullOrWhiteSpace(inner))
                {
                    var trimmed = inner.Trim();
                    if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        return trimmed;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return payload;
    }

    internal static string GetPayloadSnippet(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return payload;
        }

        return payload.Length <= 500 ? payload : payload[..500];
    }

    internal static bool ExtractLinkState(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "linked", "isLinked", "exists", "registered", "success", "data" })
            {
                if (element.TryGetProperty(key, out var nested))
                {
                    var nestedResult = ExtractLinkState(nested);
                    if (nestedResult)
                    {
                        return true;
                    }

                    if (nested.ValueKind == JsonValueKind.False)
                    {
                        return false;
                    }
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (ExtractLinkState(prop.Value))
                {
                    return true;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ExtractLinkState(item))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
