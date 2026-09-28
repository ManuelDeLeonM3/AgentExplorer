using System.Text.Json;

namespace AgentExplorer.Tools;

/// <summary>Long-term memory stored as a small JSON file in local application data.</summary>
public sealed class MemoryStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentExplorer", "agent-memory.json");

    public Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Save(string key, string value)
    {
        var all = Load();
        all[key] = value;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Indented));
    }

    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}

public static class MemoryTools
{
    public static IReadOnlyList<IAgentTool> Create(MemoryStore store) =>
    [
        new DelegateTool(
            "save_memory",
            "Saves a fact about the user to long-term memory so it can be recalled in future conversations.",
            """
            {
              "type": "object",
              "properties": {
                "key": { "type": "string", "description": "Short snake_case label, e.g. user_name" },
                "value": { "type": "string", "description": "The fact to remember, e.g. Alex" }
              },
              "required": ["key", "value"]
            }
            """,
            (args, _) =>
            {
                var key = args.GetString("key") ?? throw new ArgumentException("'key' is required.");
                var value = args.GetString("value") ?? "";
                store.Save(key, value);
                return Task.FromResult(ToolExtensions.ToJson(new { saved = true, key, value }));
            }),
        new DelegateTool(
            "get_memories",
            "Returns everything saved in long-term memory about the user.",
            """
            { "type": "object", "properties": {} }
            """,
            (_, _) =>
            {
                var all = store.Load();
                return Task.FromResult(all.Count == 0
                    ? ToolExtensions.ToJson(new { memories = all, note = "Memory is empty." })
                    : ToolExtensions.ToJson(new { memories = all }));
            }),
    ];
}
