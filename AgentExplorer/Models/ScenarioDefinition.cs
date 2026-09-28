using System.Text;
using AgentExplorer.Services;
using AgentExplorer.Tools;

namespace AgentExplorer.Models;

public sealed class ScenarioDefinition
{
    public required int Step { get; init; }
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public required string Summary { get; init; }
    public required string WhatsNew { get; init; }
    public required IReadOnlyList<string> Prompts { get; init; }
    public bool IncludeHistory { get; init; }
    public string? SystemPrompt { get; init; }
    public Func<IReadOnlyList<IAgentTool>> CreateTools { get; init; } = () => [];
    public bool HasCodeFile { get; init; }
    public bool HasMemory { get; init; }
}

public static class Scenarios
{
    public static IReadOnlyList<ScenarioDefinition> All { get; } =
    [
        new()
        {
            Step = 1,
            Key = "single",
            Title = "Single messages",
            Glyph = "\uE8BD",
            Summary = "Every message is sent to the LLM on its own, without the previous conversation.",
            WhatsNew = "LLMs are stateless: they only know what is inside the current request. Tell it your name, then ask for it.",
            Prompts = ["My name is Alex.", "What's my name?"],
        },
        new()
        {
            Step = 2,
            Key = "history",
            Title = "Message history",
            Glyph = "\uE81C",
            Summary = "The whole conversation is re-sent with every request.",
            WhatsNew = "The app resends every previous message. That is the only way the LLM can \"remember\" the conversation.",
            IncludeHistory = true,
            Prompts = ["My name is Alex.", "What's my name?"],
        },
        new()
        {
            Step = 3,
            Key = "system",
            Title = "System prompt",
            Glyph = "\uE7BE",
            Summary = "A system message at the top of every request shapes how the LLM behaves.",
            WhatsNew = "A system prompt is instructions the user doesn't see. Here it forces 3-bullet answers for a general audience.",
            IncludeHistory = true,
            SystemPrompt = "You are a friendly teacher explaining topics to a general audience. Always answer with exactly 3 short bullet points in plain language. Do not add an introduction or a conclusion.",
            Prompts = ["Explain how the internet works.", "Now explain artificial intelligence."],
        },
        new()
        {
            Step = 4,
            Key = "tool",
            Title = "Use a tool",
            Glyph = "\uE90F",
            Summary = "The request now describes a tool. The LLM can ask the agent to run it and use the result.",
            WhatsNew = "The LLM can't browse the web, but it can ask the agent to call get_current_weather. The agent loops until no more tools are requested.",
            IncludeHistory = true,
            SystemPrompt = "You are a helpful weather assistant. Use the available tools to get real, current data instead of guessing. Keep answers short.",
            CreateTools = () => [WeatherTools.GetCurrentWeather()],
            Prompts = ["What's the temperature in Tokyo, Japan?", "And in Paris, France?"],
        },
        new()
        {
            Step = 5,
            Key = "tools",
            Title = "Use multiple tools",
            Glyph = "\uE912",
            Summary = "Two tools: one gets the weather (in Celsius), the other converts between Celsius and Fahrenheit.",
            WhatsNew = "The LLM chains tools: it gets the weather first, then calls the converter, possibly several times in one turn.",
            IncludeHistory = true,
            SystemPrompt = "You are a helpful weather assistant. Use the available tools to get real, current data. The weather tool only returns Celsius: whenever you need Fahrenheit, you must call convert_temperature instead of converting yourself. Keep answers short.",
            CreateTools = () => [WeatherTools.GetCurrentWeather(), WeatherTools.ConvertTemperature()],
            Prompts = ["What's the temperature in Tokyo, Japan? Include the feels like in F and C.", "And in Paris, France?"],
        },
        new()
        {
            Step = 6,
            Key = "memory",
            Title = "Persist memory",
            Glyph = "\uE8F1",
            Summary = "Tools let the agent save facts to local storage and read them back later, even after a restart.",
            WhatsNew = "To prove the memory comes from the tools, this screen does NOT send the chat history. Try restarting the app, it still remembers.",
            IncludeHistory = false,
            HasMemory = true,
            SystemPrompt = "You are a friendly assistant with long-term memory. You cannot see previous messages, so always call get_memories before answering anything about the user. When the user shares personal information (name, preferences, etc.), call save_memory to remember it. Keep answers short.",
            CreateTools = () => MemoryTools.Create(AppState.Memory),
            Prompts = ["Hi, I'm Alex", "What's my name?"],
        },
        new()
        {
            Step = 7,
            Key = "code",
            Title = "Code assist",
            Glyph = "\uE943",
            Summary = "With tools to read and edit a file, the agent becomes a coding assistant.",
            WhatsNew = "The agent reads Program.cs, decides which edits to make, and applies them with tools. You can edit the file too.",
            IncludeHistory = true,
            HasCodeFile = true,
            SystemPrompt = $"You are a coding assistant working on a C# file named {CodeDocument.FileName}. Always call read_file first to see the current contents with line numbers, because the user may have changed it. Make changes only with the editing tools, keep the existing style and indentation, and never print the whole file. Finish with a one or two sentence summary of what you changed.",
            CreateTools = () => CodeTools.Create(AppState.CodeDocument),
            Prompts = ["Add a new entry \"Dana\" to the users array in line 14.", "Rename the variable users to teamMembers."],
        },
    ];

    /// <summary>Simplified C# that mirrors what <see cref="AgentRunner"/> does for this scenario.</summary>
    public static string BuildLoopCode(ScenarioDefinition s, IReadOnlyList<IAgentTool> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// What happens when you press Send (simplified)");
        sb.AppendLine("var messages = new List<ChatMessage>();");
        if (s.SystemPrompt is not null)
            sb.AppendLine("messages.Add(new SystemChatMessage(systemPrompt));   // instructions first");
        if (s.IncludeHistory)
            sb.AppendLine("messages.AddRange(history);                          // resend the whole conversation");
        else
            sb.AppendLine("// no history: the LLM never sees earlier messages");
        sb.AppendLine("messages.Add(new UserChatMessage(userInput));");
        sb.AppendLine();

        if (tools.Count == 0)
        {
            sb.AppendLine("// one request, one response: no tools means no loop");
            sb.AppendLine("var response = await chat.CompleteChatAsync(messages);");
            sb.AppendLine("messages.Add(new AssistantChatMessage(response));");
            sb.AppendLine("Show(response.Content);");
        }
        else
        {
            sb.AppendLine("var options = new ChatCompletionOptions();");
            foreach (var t in tools)
                sb.AppendLine($"options.Tools.Add({t.Name});   // tool definition (JSON schema)");
            sb.AppendLine();
            sb.AppendLine("while (true)                                  // the agentic loop");
            sb.AppendLine("{");
            sb.AppendLine("    var response = await chat.CompleteChatAsync(messages, options);");
            sb.AppendLine("    messages.Add(new AssistantChatMessage(response));");
            sb.AppendLine();
            sb.AppendLine("    if (response.ToolCalls.Count == 0)");
            sb.AppendLine("        break;                                // no tool calls: this is the final answer");
            sb.AppendLine();
            sb.AppendLine("    foreach (var call in response.ToolCalls)  // the LLM asked US to run tools");
            sb.AppendLine("    {");
            sb.AppendLine("        string result = call.FunctionName switch");
            sb.AppendLine("        {");
            foreach (var t in tools)
                sb.AppendLine($"            \"{t.Name}\" => {ToPascal(t.Name)}(call.FunctionArguments),");
            sb.AppendLine("        };");
            sb.AppendLine("        messages.Add(new ToolChatMessage(call.Id, result));");
            sb.AppendLine("    }");
            sb.AppendLine("}                                             // go back and let the LLM use the results");
            sb.AppendLine();
            sb.AppendLine("Show(messages.Last().Content);");
        }

        if (s.IncludeHistory)
        {
            sb.AppendLine();
            sb.AppendLine("history = messages.Without<SystemChatMessage>(); // keep for the next turn");
        }
        return sb.ToString().TrimEnd();
    }

    private static string ToPascal(string snake) =>
        string.Concat(snake.Split('_').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..])) + "Async";
}
