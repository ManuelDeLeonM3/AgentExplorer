using System.Globalization;
using System.Text.Json;
using OpenAI.Chat;

namespace AgentExplorer.Tools;

/// <summary>A function the LLM can ask the agent to run.</summary>
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    string ParametersJsonSchema { get; }
    Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken);
}

public sealed class DelegateTool(
    string name,
    string description,
    string parametersJsonSchema,
    Func<JsonElement, CancellationToken, Task<string>> invoke) : IAgentTool
{
    public string Name => name;
    public string Description => description;
    public string ParametersJsonSchema => parametersJsonSchema;
    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken) => invoke(arguments, cancellationToken);
}

public static class ToolExtensions
{
    public static readonly JsonSerializerOptions ResultJson = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ChatTool ToChatTool(this IAgentTool tool) =>
        ChatTool.CreateFunctionTool(tool.Name, tool.Description, BinaryData.FromString(tool.ParametersJsonSchema));

    public static string ToJson(object value) => JsonSerializer.Serialize(value, ResultJson);

    public static string? GetString(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;

    public static double? GetDouble(this JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static int? GetInt(this JsonElement args, string name) => args.GetDouble(name) is { } d ? (int)Math.Round(d) : null;

    public static bool? GetBool(this JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => bool.TryParse(v.ToString(), out var b) ? b : null,
        };
    }
}
