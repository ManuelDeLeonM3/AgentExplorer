using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentExplorer.Models;
using AgentExplorer.Tools;
using OpenAI.Chat;

namespace AgentExplorer.Services;

public enum AgentStage { Idle, BuildingRequest, CallingLlm, Streaming, RunningTool, Done }

/// <summary>Receives live updates from the agent loop so the UI can visualize every step.</summary>
public interface IAgentObserver
{
    void OnStageChanged(AgentStage stage, string detail);
    void OnIterationStarted(int iteration);
    void OnRequest(int iteration, string requestJson, int messageCount, int toolCount);
    void OnAssistantTextDelta(string delta);
    void OnResponse(int iteration, string responseJson, ChatFinishReason? finishReason, int toolCallCount, TimeSpan elapsed);
    void OnToolStarted(string toolName, string argumentsJson);
    void OnToolCompleted(string toolCallId, string toolName, string argumentsJson, string result, TimeSpan elapsed);
    void OnLoopFinished(int iterations);
}

/// <summary>
/// The agent, written by hand: build the request, call the LLM, run any tools it asks for,
/// send the results back, and repeat until the LLM answers without tool calls.
/// </summary>
public sealed class AgentRunner(ScenarioDefinition scenario)
{
    public const int MaxIterations = 8;

    private readonly List<ChatMessage> _history = [];

    public IReadOnlyList<IAgentTool> Tools { get; } = scenario.CreateTools();

    public void ResetConversation() => _history.Clear();

    public async Task RunAsync(string userInput, ChatClient chat, string model, IAgentObserver observer, CancellationToken ct)
    {
        // 1. Build the message list for this request.
        observer.OnStageChanged(AgentStage.BuildingRequest, "Building the request…");
        List<ChatMessage> messages = [];
        if (scenario.SystemPrompt is { } systemPrompt) messages.Add(new SystemChatMessage(systemPrompt));
        if (scenario.IncludeHistory) messages.AddRange(_history);
        messages.Add(new UserChatMessage(userInput));

        var options = new ChatCompletionOptions();
        foreach (var tool in Tools) options.Tools.Add(tool.ToChatTool());

        // 2. The agentic loop.
        for (int iteration = 1; iteration <= MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            observer.OnIterationStarted(iteration);
            observer.OnRequest(iteration, JsonFormatter.BuildRequest(model, messages, options.Tools), messages.Count, options.Tools.Count);
            observer.OnStageChanged(AgentStage.CallingLlm, $"Waiting for {model} to respond…");

            var text = new StringBuilder();
            var toolCallBuilders = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
            ChatFinishReason? finishReason = null;
            ChatTokenUsage? usage = null;
            string? completionId = null, responseModel = null;
            int chunks = 0;
            var stopwatch = Stopwatch.StartNew();

            // 3. Stream the response: text goes to the chat as it arrives, tool calls are assembled piece by piece.
            await foreach (var update in chat.CompleteChatStreamingAsync(messages, options, ct))
            {
                chunks++;
                completionId ??= update.CompletionId;
                responseModel ??= update.Model;

                foreach (var part in update.ContentUpdate)
                {
                    if (string.IsNullOrEmpty(part.Text)) continue;
                    if (text.Length == 0) observer.OnStageChanged(AgentStage.Streaming, $"{model} is writing the answer…");
                    text.Append(part.Text);
                    observer.OnAssistantTextDelta(part.Text);
                }

                foreach (var toolUpdate in update.ToolCallUpdates)
                {
                    if (!toolCallBuilders.TryGetValue(toolUpdate.Index, out var builder))
                    {
                        builder = ("", "", new StringBuilder());
                    }
                    if (!string.IsNullOrEmpty(toolUpdate.ToolCallId)) builder.Id = toolUpdate.ToolCallId;
                    if (!string.IsNullOrEmpty(toolUpdate.FunctionName))
                    {
                        builder.Name = toolUpdate.FunctionName;
                        observer.OnStageChanged(AgentStage.CallingLlm, $"{model} is asking to use the tool {builder.Name}…");
                    }
                    if (toolUpdate.FunctionArgumentsUpdate is { } argsUpdate && !argsUpdate.ToMemory().IsEmpty)
                    {
                        builder.Args.Append(argsUpdate.ToString());
                    }
                    toolCallBuilders[toolUpdate.Index] = builder;
                }

                if (update.FinishReason is { } reason) finishReason = reason;
                if (update.Usage is { } u) usage = u;
            }
            stopwatch.Stop();

            List<ChatToolCall> toolCalls = [.. toolCallBuilders.Values.Select(b =>
                ChatToolCall.CreateFunctionToolCall(b.Id, b.Name, BinaryData.FromString(b.Args.Length > 0 ? b.Args.ToString() : "{}")))];

            observer.OnResponse(
                iteration,
                JsonFormatter.BuildResponse(completionId, responseModel, chunks, text.ToString(), toolCalls, finishReason, usage),
                finishReason, toolCalls.Count, stopwatch.Elapsed);

            // 4. No tool calls? The LLM gave its final answer, so the loop ends.
            if (toolCalls.Count == 0)
            {
                messages.Add(new AssistantChatMessage(text.ToString()));
                if (scenario.IncludeHistory)
                {
                    _history.Clear();
                    _history.AddRange(messages.Where(m => m is not SystemChatMessage));
                }
                observer.OnLoopFinished(iteration);
                observer.OnStageChanged(AgentStage.Done, "Done");
                return;
            }

            // 5. Otherwise remember what the LLM asked for, run each tool, and add the results to the messages.
            var assistantMessage = new AssistantChatMessage(toolCalls);
            if (text.Length > 0) assistantMessage.Content.Add(ChatMessageContentPart.CreateTextPart(text.ToString()));
            messages.Add(assistantMessage);

            foreach (var call in toolCalls)
            {
                var args = call.FunctionArguments.ToString();
                observer.OnStageChanged(AgentStage.RunningTool, $"Running tool {call.FunctionName}…");
                observer.OnToolStarted(call.FunctionName, args);
                var toolStopwatch = Stopwatch.StartNew();
                var result = await InvokeToolAsync(call.FunctionName, args, ct);
                observer.OnToolCompleted(call.Id, call.FunctionName, args, result, toolStopwatch.Elapsed);
                messages.Add(new ToolChatMessage(call.Id, result));
            }
            // ...and go around the loop again so the LLM can use the tool results.
        }

        throw new InvalidOperationException($"The agent stopped after {MaxIterations} loop iterations without a final answer.");
    }

    private async Task<string> InvokeToolAsync(string name, string argumentsJson, CancellationToken ct)
    {
        var tool = Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null) return ToolExtensions.ToJson(new { error = $"Unknown tool '{name}'." });
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return await tool.InvokeAsync(doc.RootElement.Clone(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Errors are returned to the LLM as a tool result so it can recover or explain the problem.
            return ToolExtensions.ToJson(new { error = ex.Message });
        }
    }
}
