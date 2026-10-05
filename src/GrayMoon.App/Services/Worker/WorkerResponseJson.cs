using System.Text.Json;

namespace GrayMoon.App.Services.Worker;

/// <summary>JSON options for deserializing worker responses. Worker sends PascalCase; API may use camelCase.</summary>
public static class WorkerResponseJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Deserialize worker response data (object or JsonElement) to T. Use for worker command responses.</summary>
    public static T? DeserializeWorkerResponse<T>(object? data) where T : class
    {
        if (data == null)
            return null;
        try
        {
            var json = data is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(data);
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch
        {
            return null;
        }
    }
}
