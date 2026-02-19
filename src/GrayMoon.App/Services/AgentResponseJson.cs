using System.Text.Json;

namespace GrayMoon.App.Services;

/// <summary>JSON options for deserializing agent responses. Agent sends PascalCase; API may use camelCase.</summary>
public static class AgentResponseJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Deserialize agent response data (object or JsonElement) to T. Use for agent command responses.</summary>
    public static T? DeserializeAgentResponse<T>(object? data) where T : class
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
