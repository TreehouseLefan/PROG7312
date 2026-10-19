using System.Net.Http.Json;
using System.Text.Json;

namespace SmartX.Client.Services;

/// <summary>Success value or a readable error extracted from a ProblemDetails response.</summary>
public sealed class ApiResult<T>
{
    public bool Success { get; private init; }
    public T? Value { get; private init; }
    public string? Error { get; private init; }
    public IReadOnlyList<string> Details { get; private init; } = Array.Empty<string>();

    public static ApiResult<T> Failure(string message) => new() { Success = false, Error = message };

    public static async Task<ApiResult<T>> FromResponseAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            T? value = default;
            if (response.Content.Headers.ContentLength != 0)
            {
                value = await response.Content.ReadFromJsonAsync<T>();
            }

            return new ApiResult<T> { Success = true, Value = value };
        }

        string title = $"{(int)response.StatusCode} {response.ReasonPhrase}";
        var details = new List<string>();

        try
        {
            string body = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("title", out JsonElement titleElement) && titleElement.ValueKind == JsonValueKind.String)
                    {
                        title = titleElement.GetString() ?? title;
                    }

                    if (root.TryGetProperty("error", out JsonElement errorElement) && errorElement.ValueKind == JsonValueKind.String)
                    {
                        details.Add(errorElement.GetString() ?? string.Empty);
                    }

                    if (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty property in errors.EnumerateObject())
                        {
                            if (property.Value.ValueKind != JsonValueKind.Array)
                            {
                                continue;
                            }

                            foreach (JsonElement message in property.Value.EnumerateArray())
                            {
                                details.Add($"{property.Name}: {message.GetString()}");
                            }
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Body was not JSON; the status line is enough.
        }

        return new ApiResult<T> { Success = false, Error = title, Details = details };
    }
}
