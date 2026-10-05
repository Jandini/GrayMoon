using System.Text.Json;

namespace GrayMoon.Worker.Services;

public static class WorkerJsonOptions
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}
