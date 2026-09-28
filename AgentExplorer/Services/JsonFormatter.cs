using System.ClientModel.Primitives;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExplorer.Tools;
using OpenAI.Chat;

namespace AgentExplorer.Services;

/// <summary>Produces the pretty JSON shown in the "Agent activity" panel.</summary>
public static class JsonFormatter
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Builds the chat completions request body exactly as the SDK sends it on the wire.</summary>
    public static string BuildRequest(string model, IEnumerable<ChatMessage> messages, IEnumerable<ChatTool> tools)
    {
        var root = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = new JsonArray([.. messages.Select(m => ToNode(m))]),
        };
        var toolArray = new JsonArray([.. tools.Select(t => ToNode(t))]);
        if (toolArray.Count > 0) root["tools"] = toolArray;
        return root.ToJsonString(Pretty);
    }

    public static string BuildToolDefinition(IAgentTool tool) => ToNode(tool.ToChatTool())!.ToJsonString(Pretty);

    public static string BuildResponse(
        string? id, string? model, int chunks, string text, IReadOnlyList<ChatToolCall> toolCalls,
        ChatFinishReason? finishReason, ChatTokenUsage? usage)
    {
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text.Length > 0 ? text : null,
        };
        if (toolCalls.Count > 0)
        {
            message["tool_calls"] = new JsonArray([.. toolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = c.FunctionName,
                    ["arguments"] = c.FunctionArguments.ToString(),
                },
            })]);
        }

        var root = new JsonObject
        {
            ["id"] = id,
            ["object"] = $"chat.completion (assembled from {chunks} streamed chunks)",
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = message,
                ["finish_reason"] = finishReason switch
                {
                    ChatFinishReason.Stop => "stop",
                    ChatFinishReason.ToolCalls => "tool_calls",
                    ChatFinishReason.Length => "length",
                    ChatFinishReason.ContentFilter => "content_filter",
                    ChatFinishReason.FunctionCall => "function_call",
                    _ => null,
                },
            }),
        };
        if (usage is not null)
        {
            var usageNode = new JsonObject
            {
                ["prompt_tokens"] = usage.InputTokenCount,
                ["completion_tokens"] = usage.OutputTokenCount,
                ["total_tokens"] = usage.TotalTokenCount,
            };
            if (usage.OutputTokenDetails is { } details)
            {
                usageNode["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = details.ReasoningTokenCount };
            }
            root["usage"] = usageNode;
        }
        return root.ToJsonString(Pretty);
    }

    /// <summary>Pretty-prints JSON if possible, otherwise returns the text unchanged.</summary>
    public static string Format(string text)
    {
        try
        {
            return JsonNode.Parse(text)?.ToJsonString(Pretty) ?? text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    public static JsonNode ParseOrString(string text)
    {
        try
        {
            return JsonNode.Parse(text) ?? JsonValue.Create(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    public static string Serialize(JsonNode node) => node.ToJsonString(Pretty);

    private static JsonNode? ToNode<T>(T model) where T : IPersistableModel<T> =>
        JsonNode.Parse(ModelReaderWriter.Write(model).ToString());
}
