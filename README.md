# Agent Explorer

A native Windows app (WinUI 3, Windows App SDK 1.8, .NET 10 / C# 14) that shows **how AI agents work**, step by step.

Each of the 7 screens has:

- **Conversation**: chat with the agent. Replies stream in, and you can use the suggested prompts.
- **Agent loop**: a live pipeline (User message → Build request → LLM → Run tools → Answer) that highlights what the agent is doing right now.
- **Agent activity (JSON)**: one collapsible section per turn and loop iteration. Each shows the full request sent to the LLM (system prompt, message history, tools), the assembled streamed response, and every tool call with its arguments and result.
- **Behind the curtain**: simplified C# of the agentic loop, the system prompt and the tool definitions.

| # | Screen | What it teaches |
|---|---|---|
| 1 | Single messages | LLMs have no memory. Each request stands alone. |
| 2 | Message history | "Memory" is just resending the conversation. |
| 3 | System prompt | Hidden instructions shape behavior (3 bullets for a general audience). |
| 4 | Use a tool | The LLM asks, the agent runs `get_current_weather`, and the loop continues. |
| 5 | Use multiple tools | The LLM chains weather and `convert_temperature` calls. |
| 6 | Persist memory | `save_memory` / `get_memories` store facts in a local JSON file. |
| 7 | Code assist | File tools let the agent read and edit an editable `Program.cs`. |

The model (GPT-5 Nano / Mini / GPT-5 by default) is chosen globally from the title bar. Sign-in uses Entra ID through `DefaultAzureCredential`, so there are no API keys.

## Getting started

See **[SETUP.md](SETUP.md)** for the Azure configuration. Then open `AgentExplorer.slnx`, select **x64**, and press **F5**.

## Code map

| File | Purpose |
|---|---|
| `Services/AgentRunner.cs` | **The hand-written agent loop**: build messages → stream the LLM response → run tool calls → repeat. |
| `Services/JsonFormatter.cs` | Builds the request/response JSON shown in the activity panel. |
| `Services/AuthService.cs` | Entra ID sign-in with `DefaultAzureCredential`. |
| `Services/AppState.cs` | Global state and the `ChatClient` factory (Azure OpenAI v1 endpoint). |
| `Models/ScenarioDefinition.cs` | The 7 screens: prompts, system prompts, tools, history mode. |
| `Tools/*.cs` | Weather (Open-Meteo), temperature conversion, memory and code-editing tools. |
| `Views/ScenarioView.xaml(.cs)` | The shared screen layout and live visualization. |
