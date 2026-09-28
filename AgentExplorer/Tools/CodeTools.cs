using System.Text;
using System.Text.RegularExpressions;

namespace AgentExplorer.Tools;

/// <summary>The single code file the Code assist agent works on. Lines are separated by '\n'.</summary>
public sealed class CodeDocument
{
    public const string FileName = "Program.cs";

    public const string InitialText = """
        using System;

        namespace TeamDirectory;

        public static class Program
        {
            public static void Main()
            {
                string[] users =
                {
                    "Alice",
                    "Bob",
                    "Charlie",
                };

                Console.WriteLine($"Team size: {users.Length}");
                foreach (var user in users)
                {
                    Console.WriteLine($"- {user}");
                }
            }
        }
        """;

    public string Text { get; private set; } = Normalize(InitialText);

    /// <summary>Raised when the text changes. The argument is true when the agent made the change.</summary>
    public event EventHandler<bool>? Changed;

    public List<string> Lines => [.. Text.Split('\n')];

    public void SetText(string text, bool byAgent)
    {
        text = Normalize(text);
        if (text == Text) return;
        Text = text;
        Changed?.Invoke(this, byAgent);
    }

    public void Reset() => SetText(InitialText, byAgent: true);

    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}

public static class CodeTools
{
    public static IReadOnlyList<IAgentTool> Create(CodeDocument doc) =>
    [
        new DelegateTool(
            "read_file",
            $"Returns the current contents of {CodeDocument.FileName} with 1-based line numbers. Always read the file before editing it.",
            """
            { "type": "object", "properties": {} }
            """,
            (_, _) =>
            {
                var lines = doc.Lines;
                var sb = new StringBuilder($"{CodeDocument.FileName} ({lines.Count} lines)\n");
                for (int i = 0; i < lines.Count; i++) sb.Append($"{i + 1,3} | {lines[i]}\n");
                return Task.FromResult(sb.ToString());
            }),
        new DelegateTool(
            "insert_lines",
            "Inserts new lines so that the first new line ends up at 'line'. The existing line at that position (and everything after it) moves down. Use line = line_count + 1 to append.",
            """
            {
              "type": "object",
              "properties": {
                "line": { "type": "integer", "description": "1-based line number for the first inserted line" },
                "text": { "type": "string", "description": "The text to insert, including indentation. May contain multiple lines." }
              },
              "required": ["line", "text"]
            }
            """,
            (args, _) =>
            {
                var lines = doc.Lines;
                var line = args.GetInt("line") ?? throw new ArgumentException("'line' is required.");
                if (line < 1 || line > lines.Count + 1) return Error($"line must be between 1 and {lines.Count + 1}.");
                var newLines = SplitLines(args.GetString("text") ?? "");
                lines.InsertRange(line - 1, newLines);
                doc.SetText(string.Join('\n', lines), byAgent: true);
                return Ok($"Inserted {newLines.Length} line(s) at line {line}.", lines.Count);
            }),
        new DelegateTool(
            "replace_lines",
            "Replaces the lines from start_line to end_line (inclusive) with new text.",
            """
            {
              "type": "object",
              "properties": {
                "start_line": { "type": "integer" },
                "end_line": { "type": "integer" },
                "text": { "type": "string", "description": "Replacement text, including indentation. May contain multiple lines." }
              },
              "required": ["start_line", "end_line", "text"]
            }
            """,
            (args, _) =>
            {
                var lines = doc.Lines;
                var (start, end) = (args.GetInt("start_line") ?? 0, args.GetInt("end_line") ?? 0);
                if (start < 1 || end < start || end > lines.Count) return Error($"Invalid range. The file has {lines.Count} lines.");
                var newLines = SplitLines(args.GetString("text") ?? "");
                lines.RemoveRange(start - 1, end - start + 1);
                lines.InsertRange(start - 1, newLines);
                doc.SetText(string.Join('\n', lines), byAgent: true);
                return Ok($"Replaced lines {start}-{end} with {newLines.Length} line(s).", lines.Count);
            }),
        new DelegateTool(
            "delete_lines",
            "Deletes the lines from start_line to end_line (inclusive).",
            """
            {
              "type": "object",
              "properties": {
                "start_line": { "type": "integer" },
                "end_line": { "type": "integer" }
              },
              "required": ["start_line", "end_line"]
            }
            """,
            (args, _) =>
            {
                var lines = doc.Lines;
                var (start, end) = (args.GetInt("start_line") ?? 0, args.GetInt("end_line") ?? 0);
                if (start < 1 || end < start || end > lines.Count) return Error($"Invalid range. The file has {lines.Count} lines.");
                lines.RemoveRange(start - 1, end - start + 1);
                doc.SetText(string.Join('\n', lines), byAgent: true);
                return Ok($"Deleted lines {start}-{end}.", lines.Count);
            }),
        new DelegateTool(
            "replace_text",
            "Finds and replaces text everywhere in the file. Use whole_word = true to rename identifiers safely.",
            """
            {
              "type": "object",
              "properties": {
                "find": { "type": "string" },
                "replace": { "type": "string" },
                "whole_word": { "type": "boolean", "description": "Only match whole words. Defaults to true." }
              },
              "required": ["find", "replace"]
            }
            """,
            (args, _) =>
            {
                var find = args.GetString("find");
                if (string.IsNullOrEmpty(find)) return Error("'find' is required.");
                var replace = args.GetString("replace") ?? "";
                var pattern = Regex.Escape(find);
                if (args.GetBool("whole_word") ?? true) pattern = $@"\b{pattern}\b";
                var regex = new Regex(pattern);
                var count = regex.Matches(doc.Text).Count;
                doc.SetText(regex.Replace(doc.Text, replace.Replace("$", "$$")), byAgent: true);
                return Ok($"Replaced {count} occurrence(s) of '{find}' with '{replace}'.", doc.Lines.Count);
            }),
    ];

    private static string[] SplitLines(string text) => CodeDocument.Normalize(text).TrimEnd('\n').Split('\n');

    private static Task<string> Ok(string message, int lineCount) =>
        Task.FromResult(ToolExtensions.ToJson(new { ok = true, message, line_count = lineCount }));

    private static Task<string> Error(string message) =>
        Task.FromResult(ToolExtensions.ToJson(new { ok = false, error = message }));
}
