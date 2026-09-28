using System.ClientModel;
using System.Text.Json.Nodes;
using AgentExplorer.Models;
using AgentExplorer.Services;
using AgentExplorer.Tools;
using Azure.Identity;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenAI.Chat;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace AgentExplorer.Views;

/// <summary>One learning screen: conversation on the left, what the agent is doing on the right.</summary>
public sealed partial class ScenarioView : UserControl, IAgentObserver
{
    private readonly ScenarioDefinition _scenario;
    private readonly AgentRunner _runner;
    private readonly Dictionary<string, (Border Chip, FontIcon Icon, TextBlock Label)> _stages = [];

    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _updatingEditor;
    private int _turn;
    private string _currentModel = "";

    private TextBlock? _assistantText;
    private Expander? _turnExpander;
    private StackPanel? _turnBody;
    private StackPanel? _iterationBody;
    private TextBlock? _iterationSubtitle;
    private ProgressRing? _iterationRing;
    private FontIcon? _iterationDoneIcon;

    public ScenarioView(ScenarioDefinition scenario)
    {
        _scenario = scenario;
        _runner = new AgentRunner(scenario);
        InitializeComponent();

        HeaderIcon.Glyph = scenario.Glyph;
        StepText.Text = $"STEP {scenario.Step} OF {Scenarios.All.Count}";
        TitleText.Text = scenario.Title;
        SummaryText.Text = scenario.Summary;
        WhatsNewText.Text = scenario.WhatsNew;

        foreach (var prompt in scenario.Prompts) SuggestionsPanel.Children.Add(CreateSuggestion(prompt));

        BuildPipeline();
        BuildCurtain();
        UpdateModelBadge();
        AppState.ModelChanged += (_, _) => UpdateModelBadge();

        if (scenario.HasMemory)
        {
            ExtraActionButton.Visibility = Visibility.Visible;
            ExtraActionIcon.Glyph = "\uE74D";
            ExtraActionText.Text = "Clear memory";
        }

        if (scenario.HasCodeFile)
        {
            ExtraActionButton.Visibility = Visibility.Visible;
            ExtraActionIcon.Glyph = "\uE72C";
            ExtraActionText.Text = "Reset file";
            CodeTab.Visibility = Visibility.Visible;
            Tabs.SelectedItem = CodeTab;
            LoadEditorFromDocument();
            AppState.CodeDocument.Changed += (_, byAgent) =>
            {
                if (byAgent) LoadEditorFromDocument();
            };
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Sending â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task SendAsync(string text)
    {
        text = text.Trim();
        if (_busy || text.Length == 0) return;

        if (!AppState.Settings.IsConfigured)
        {
            AddInfo(InfoBarSeverity.Warning, "Setup needed", "Add your Azure OpenAI endpoint in Settings first. See SETUP.md.");
            if (AppState.Window is { } window) await window.ShowSettingsAsync();
            return;
        }

        SetBusy(true);
        InputBox.Text = "";
        _currentModel = AppState.SelectedModel.Deployment;
        _assistantText = null;
        _cts = new CancellationTokenSource();

        AddUserBubble(text);
        StartTurn(text);
        SetStage("user");

        try
        {
            if (!AppState.Auth.IsSignedIn)
            {
                ShowStatus("Signing in with Entra IDâ€¦");
                await AppState.Auth.EnsureSignedInAsync(_cts.Token);
            }
            var chat = AppState.CreateChatClient(_currentModel);
            await _runner.RunAsync(text, chat, _currentModel, this, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            AddInfo(InfoBarSeverity.Informational, "Stopped", "The request was cancelled.");
            AddTurnNote("\uE71A", "Cancelled by the user.");
        }
        catch (Exception ex)
        {
            var (title, message) = DescribeError(ex);
            AddInfo(InfoBarSeverity.Error, title, message);
            AddTurnNote("\uE783", $"{title}: {message}");
        }
        finally
        {
            FinishIterationIndicator();
            SetStage(null);
            IterationText.Text = "Idle";
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
            InputBox.Focus(FocusState.Programmatic);
        }
    }

    private static (string Title, string Message) DescribeError(Exception ex) => ex switch
    {
        CredentialUnavailableException or AuthenticationFailedException =>
            ("Sign-in failed", "Sign in to Visual Studio or run 'az login', then try again.\n" + ex.Message),
        ClientResultException { Status: 401 or 403 } =>
            ("Access denied", "Your account needs the 'Cognitive Services OpenAI User' role on the Azure OpenAI resource (it can take a few minutes to apply).\n" + ex.Message),
        ClientResultException { Status: 404 } =>
            ("Deployment not found", $"Check that a deployment named '{AppState.SelectedModel.Deployment}' exists and the endpoint is correct.\n" + ex.Message),
        ClientResultException { Status: 429 } =>
            ("Rate limited", "The deployment's tokens-per-minute quota was exceeded. Wait a moment or raise the quota.\n" + ex.Message),
        _ => ("Something went wrong", ex.Message),
    };

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SendButton.IsEnabled = !busy;
        NewConversationButton.IsEnabled = !busy;
        ExtraActionButton.IsEnabled = !busy;
        foreach (var child in SuggestionsPanel.Children.OfType<Button>()) child.IsEnabled = !busy;
        StatusBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusRing.IsActive = busy;
    }

    private void ShowStatus(string text) => StatusText.Text = text;

    private void Send_Click(object sender, RoutedEventArgs e) => _ = SendAsync(InputBox.Text);

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = SendAsync(InputBox.Text);
        }
    }

    private void NewConversation_Click(object sender, RoutedEventArgs e)
    {
        _runner.ResetConversation();
        ChatPanel.Children.Clear();
        ActivityPanel.Children.Clear();
        ChatEmptyState.Visibility = Visibility.Visible;
        ActivityEmptyState.Visibility = Visibility.Visible;
        _turn = 0;
    }

    private void ExtraActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scenario.HasMemory)
        {
            AppState.Memory.Clear();
            AddInfo(InfoBarSeverity.Success, "Memory cleared", "The agent has forgotten everything it saved.");
            BuildCurtain();
        }
        else if (_scenario.HasCodeFile)
        {
            AppState.CodeDocument.Reset();
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ IAgentObserver â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public void OnStageChanged(AgentStage stage, string detail)
    {
        ShowStatus(detail);
        SetStage(stage switch
        {
            AgentStage.BuildingRequest => "build",
            AgentStage.CallingLlm or AgentStage.Streaming => "llm",
            AgentStage.RunningTool => "tools",
            AgentStage.Done => "answer",
            _ => null,
        });
    }

    public void OnIterationStarted(int iteration)
    {
        FinishIterationIndicator();
        IterationText.Text = $"Iteration {iteration}";

        _iterationRing = new ProgressRing { IsActive = true, Width = 16, Height = 16 };
        _iterationDoneIcon = new FontIcon { Glyph = "\uE73E", FontSize = 14, Foreground = Brush("SystemFillColorSuccessBrush"), Visibility = Visibility.Collapsed };
        _iterationSubtitle = new TextBlock { Foreground = Brush("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center, Style = GetStyle("CaptionTextBlockStyle") };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(_iterationRing);
        header.Children.Add(_iterationDoneIcon);
        header.Children.Add(new TextBlock { Text = $"Loop iteration {iteration}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(_iterationSubtitle);

        _iterationBody = new StackPanel { Spacing = 6 };
        _turnBody?.Children.Add(new Expander
        {
            Header = header,
            Content = _iterationBody,
            IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });
        ScrollToEnd(ActivityScroll);
    }

    public void OnRequest(int iteration, string requestJson, int messageCount, int toolCount)
    {
        var subtitle = $"{messageCount} message{Plural(messageCount)}" + (toolCount > 0 ? $" Â· {toolCount} tool{Plural(toolCount)}" : "");
        _iterationBody?.Children.Add(CreateJsonSection("\uE724", "Request sent to the LLM", subtitle, requestJson, expanded: false));
        _iterationSubtitle!.Text = $"calling {_currentModel}â€¦";
    }

    public void OnAssistantTextDelta(string delta)
    {
        _assistantText ??= AddAssistantBubble();
        _assistantText.Text += delta;
        ScrollToEnd(ChatScroll);
    }

    public void OnResponse(int iteration, string responseJson, ChatFinishReason? finishReason, int toolCallCount, TimeSpan elapsed)
    {
        var reason = finishReason == ChatFinishReason.ToolCalls ? "tool_calls" : finishReason?.ToString().ToLowerInvariant() ?? "unknown";
        var subtitle = $"finish_reason: {reason} Â· {elapsed.TotalSeconds:0.0}s";
        _iterationBody?.Children.Add(CreateJsonSection("\uE8BD", "LLM response", subtitle, responseJson, expanded: true));
        _iterationSubtitle!.Text = toolCallCount > 0
            ? $"LLM requested {toolCallCount} tool call{Plural(toolCallCount)}"
            : "LLM answered, no tool calls";
        ScrollToEnd(ActivityScroll);
    }

    public void OnToolStarted(string toolName, string argumentsJson) => AddToolChip(toolName, argumentsJson);

    public void OnToolCompleted(string toolCallId, string toolName, string argumentsJson, string result, TimeSpan elapsed)
    {
        var json = JsonFormatter.Serialize(new JsonObject
        {
            ["tool_call_id"] = toolCallId,
            ["name"] = toolName,
            ["arguments"] = JsonFormatter.ParseOrString(argumentsJson),
            ["result"] = JsonFormatter.ParseOrString(result),
        });
        _iterationBody?.Children.Add(CreateJsonSection("\uE90F", $"Tool call Â· {toolName}", $"ran locally in {elapsed.TotalMilliseconds:0} ms, result added to messages", json, expanded: true));
        if (_scenario.HasMemory) BuildCurtain();
        ScrollToEnd(ActivityScroll);
    }

    public void OnLoopFinished(int iterations)
    {
        FinishIterationIndicator();
        var text = _runner.Tools.Count == 0
            ? "No tools available, so a single LLM call is all it takes."
            : $"No tool calls in the last response, so the loop ended after {iterations} iteration{Plural(iterations)}.";
        AddTurnNote("\uE73E", text);
        if (_assistantText is null) AddAssistantBubble().Text = "(empty response)";
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Chat UI â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private Button CreateSuggestion(string prompt)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        content.Children.Add(new FontIcon { Glyph = "\uE8F2", FontSize = 13, Foreground = Brush("AccentTextFillColorPrimaryBrush") });
        content.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 7, 12, 7),
        };
        ToolTipService.SetToolTip(button, "Send this prompt");
        button.Click += (_, _) => _ = SendAsync(prompt);
        return button;
    }

    private void AddUserBubble(string text)
    {
        ChatEmptyState.Visibility = Visibility.Collapsed;
        ChatPanel.Children.Add(new Border
        {
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Foreground = Brush("TextOnAccentFillColorPrimaryBrush"),
            },
            Background = Brush("AccentFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(16, 16, 4, 16),
            Padding = new Thickness(14, 9, 14, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(56, 4, 0, 0),
        });
        ScrollToEnd(ChatScroll);
    }

    private TextBlock AddAssistantBubble()
    {
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        caption.Children.Add(new FontIcon { Glyph = "\uE99A", FontSize = 12, Foreground = Brush("AccentTextFillColorPrimaryBrush") });
        caption.Children.Add(new TextBlock { Text = $"Agent Â· {_currentModel}", Style = GetStyle("CaptionTextBlockStyle"), Foreground = Brush("TextFillColorSecondaryBrush") });
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(caption);
        panel.Children.Add(body);
        ChatPanel.Children.Add(new Border
        {
            Child = panel,
            Background = Brush("ControlFillColorDefaultBrush"),
            BorderBrush = Brush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16, 16, 16, 4),
            Padding = new Thickness(14, 9, 14, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 56, 0),
        });
        ScrollToEnd(ChatScroll);
        return body;
    }

    private void AddToolChip(string toolName, string argumentsJson)
    {
        var args = JsonFormatter.ParseOrString(argumentsJson);
        var argText = args is JsonObject obj
            ? string.Join(", ", obj.Select(p => $"{p.Key}: {p.Value?.ToJsonString()}"))
            : argumentsJson;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new FontIcon { Glyph = "\uE90F", FontSize = 12, Foreground = Brush("AccentTextFillColorPrimaryBrush") });
        panel.Children.Add(new TextBlock
        {
            Text = $"{toolName}({argText})",
            FontFamily = CodeFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextFillColorSecondaryBrush"),
        });
        ChatPanel.Children.Add(new Border
        {
            Child = panel,
            Background = Brush("SubtleFillColorSecondaryBrush"),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 12, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        // Text streamed after a tool call goes into a fresh bubble below the chip.
        _assistantText = null;
        ScrollToEnd(ChatScroll);
    }

    private void AddInfo(InfoBarSeverity severity, string title, string message)
    {
        ChatEmptyState.Visibility = Visibility.Collapsed;
        ChatPanel.Children.Add(new InfoBar { Severity = severity, Title = title, Message = message, IsOpen = true, IsClosable = true });
        ScrollToEnd(ChatScroll);
    }

    private void UpdateModelBadge() => ModelBadge.Text = AppState.SelectedModel.DisplayName;

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Activity UI â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void StartTurn(string userText)
    {
        ActivityEmptyState.Visibility = Visibility.Collapsed;
        if (_turnExpander is not null) _turnExpander.IsExpanded = false;
        _turn++;

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new TextBlock { Text = $"Turn {_turn}", Style = GetStyle("BodyStrongTextBlockStyle") });
        header.Children.Add(new TextBlock
        {
            Text = $"\u201C{userText}\u201D  Â·  {_currentModel}",
            Style = GetStyle("CaptionTextBlockStyle"),
            Foreground = Brush("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        _turnBody = new StackPanel { Spacing = 8 };
        _turnExpander = new Expander
        {
            Header = header,
            Content = _turnBody,
            IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ActivityPanel.Children.Add(_turnExpander);

        if (!_scenario.IncludeHistory && _turn > 1)
        {
            AddTurnNote("\uE7BA", "History is NOT included: the request below only contains this message, not the earlier ones.");
        }
        ScrollToEnd(ActivityScroll);
    }

    private void AddTurnNote(string glyph, string text)
    {
        if (_turnBody is null) return;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = Brush("AccentTextFillColorPrimaryBrush") });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 });
        _turnBody.Children.Add(new Border
        {
            Child = panel,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            Background = Brush("SubtleFillColorSecondaryBrush"),
        });
        ScrollToEnd(ActivityScroll);
    }

    private void FinishIterationIndicator()
    {
        if (_iterationRing is null) return;
        _iterationRing.IsActive = false;
        _iterationRing.Visibility = Visibility.Collapsed;
        _iterationDoneIcon!.Visibility = Visibility.Visible;
        _iterationRing = null;
    }

    private Expander CreateJsonSection(string glyph, string title, string subtitle, string json, bool expanded)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = Brush("AccentTextFillColorPrimaryBrush") });
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = subtitle, Style = GetStyle("CaptionTextBlockStyle"), Foreground = Brush("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });

        return new Expander
        {
            Header = header,
            Content = CreateCodeBlock(json, copyable: true),
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Behind the curtain â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void BuildCurtain()
    {
        CurtainContent.Children.Clear();

        AddCurtainSection("The agentic loop", "Simplified C# of what the app does every time you press Send. The real code is in Services/AgentRunner.cs.");
        CurtainContent.Children.Add(CreateCodeBlock(Scenarios.BuildLoopCode(_scenario, _runner.Tools), copyable: true));

        AddCurtainSection("System prompt", _scenario.SystemPrompt is null
            ? "None. Nothing tells the LLM how to behave, so it uses its default personality."
            : "Sent as the first message of every request. The user never sees it.");
        if (_scenario.SystemPrompt is not null)
            CurtainContent.Children.Add(CreateCodeBlock(_scenario.SystemPrompt, copyable: true, wrap: true));

        AddCurtainSection("Message history", _scenario.IncludeHistory
            ? "Every request contains the full conversation so far (user messages, assistant answers, tool calls and tool results). New conversation clears it."
            : "Not sent. Each request only contains the newest user message, so the LLM starts from scratch every time.");

        AddCurtainSection("Tools", _runner.Tools.Count == 0
            ? "None. The LLM can only use what it learned during training and what is in the request."
            : "These definitions are sent with every request. The LLM never runs them itself: it replies with a tool call, the agent runs the code locally and sends the result back.");
        foreach (var tool in _runner.Tools)
            CurtainContent.Children.Add(CreateCodeBlock(JsonFormatter.BuildToolDefinition(tool), copyable: true));

        if (_scenario.HasMemory)
        {
            AddCurtainSection("Stored memory", $"Saved on disk at {AppState.Memory.FilePath}");
            var memory = AppState.Memory.Load();
            CurtainContent.Children.Add(CreateCodeBlock(
                memory.Count == 0 ? "{ }  // empty" : JsonFormatter.Format(System.Text.Json.JsonSerializer.Serialize(memory)),
                copyable: false));
        }
    }

    private void AddCurtainSection(string title, string description)
    {
        CurtainContent.Children.Add(new TextBlock { Text = title, Style = GetStyle("SectionTitleStyle"), Margin = new Thickness(0, 10, 0, 0) });
        CurtainContent.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextFillColorSecondaryBrush") });
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selected = sender.SelectedItem;
        CodePanel.Visibility = selected == CodeTab ? Visibility.Visible : Visibility.Collapsed;
        ActivityScroll.Visibility = selected == ActivityTab ? Visibility.Visible : Visibility.Collapsed;
        CurtainPanel.Visibility = selected == CurtainTab ? Visibility.Visible : Visibility.Collapsed;
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Pipeline â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void BuildPipeline()
    {
        var hasTools = _runner.Tools.Count > 0;
        (string Key, string Glyph, string Label)[] steps =
        [
            ("user", "\uE77B", "User message"),
            ("build", "\uE8A5", "Build request"),
            ("llm", "\uE99A", "LLM"),
            ("tools", "\uE90F", hasTools ? "Run tools" : "No tools"),
            ("answer", "\uE73E", "Answer"),
        ];

        for (int i = 0; i < steps.Length; i++)
        {
            var (key, glyph, label) = steps[i];
            if (i > 0)
            {
                PipelinePanel.Children.Add(new FontIcon { Glyph = "\uE76C", FontSize = 10, Foreground = Brush("TextFillColorTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            }
            var icon = new FontIcon { Glyph = glyph, FontSize = 13 };
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(icon);
            content.Children.Add(text);
            var chip = new Border
            {
                Child = content,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 5, 12, 6),
                BorderThickness = new Thickness(1),
                Opacity = key == "tools" && !hasTools ? 0.4 : 1,
            };
            _stages[key] = (chip, icon, text);
            PipelinePanel.Children.Add(chip);
        }

        LoopHintText.Text = hasTools ? "\u21BB  LLM \u2194 tools repeat until no more tool calls" : "One request, one response";
        SetStage(null);
    }

    private void SetStage(string? activeKey)
    {
        foreach (var (key, (chip, icon, label)) in _stages)
        {
            var active = key == activeKey;
            chip.Background = active ? Brush("AccentFillColorDefaultBrush") : Brush("SubtleFillColorTransparentBrush");
            chip.BorderBrush = active ? Brush("AccentFillColorDefaultBrush") : Brush("CardStrokeColorDefaultBrush");
            var foreground = active ? Brush("TextOnAccentFillColorPrimaryBrush") : Brush("TextFillColorSecondaryBrush");
            icon.Foreground = foreground;
            label.Foreground = foreground;
            label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Code editor â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void LoadEditorFromDocument()
    {
        _updatingEditor = true;
        CodeEditor.Text = AppState.CodeDocument.Text.Replace("\n", "\r");
        _updatingEditor = false;
        UpdateLineNumbers();
    }

    private void CodeEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingEditor) AppState.CodeDocument.SetText(CodeEditor.Text, byAgent: false);
        UpdateLineNumbers();
    }

    private void CodeEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        var before = CodeEditor.Text[..Math.Min(CodeEditor.SelectionStart, CodeEditor.Text.Length)];
        var line = before.Count(c => c == '\r') + 1;
        var column = before.Length - before.LastIndexOf('\r');
        CaretText.Text = $"Ln {line}, Col {column}";
    }

    private void UpdateLineNumbers()
    {
        var count = CodeDocument.Normalize(CodeEditor.Text).Split('\n').Length;
        LineNumbers.Text = string.Join("\n", Enumerable.Range(1, count));
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static FontFamily CodeFont => (FontFamily)Application.Current.Resources["CodeFontFamily"];

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static Style GetStyle(string key) => (Style)Application.Current.Resources[key];

    private static string Plural(int n) => n == 1 ? "" : "s";

    private static FrameworkElement CreateCodeBlock(string code, bool copyable, bool wrap = false)
    {
        var text = new TextBlock
        {
            FontFamily = CodeFont,
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        var lines = code.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) text.Inlines.Add(new LineBreak());
            var line = lines[i];
            var comment = wrap ? -1 : line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0 && !line[..comment].Contains("://"))
            {
                text.Inlines.Add(new Run { Text = line[..comment] });
                text.Inlines.Add(new Run { Text = line[comment..], Foreground = Brush("SystemFillColorSuccessBrush") });
            }
            else
            {
                text.Inlines.Add(new Run { Text = line });
            }
        }

        var grid = new Grid();
        grid.Children.Add(text);
        if (copyable)
        {
            var copy = new Button
            {
                Content = new FontIcon { Glyph = "\uE8C8", FontSize = 12 },
                Padding = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Background = Brush("SubtleFillColorTransparentBrush"),
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(copy, "Copy");
            copy.Click += (_, _) =>
            {
                var package = new DataPackage();
                package.SetText(code);
                Clipboard.SetContent(package);
            };
            grid.Children.Add(copy);
            text.Margin = new Thickness(0, 0, 32, 0);
        }

        return new Border
        {
            Child = grid,
            Padding = new Thickness(12, 10, 8, 10),
            CornerRadius = new CornerRadius(6),
            Background = Brush("SolidBackgroundFillColorBaseBrush"),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
        };
    }

    private void ScrollToEnd(ScrollViewer viewer) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            viewer.UpdateLayout();
            viewer.ChangeView(null, viewer.ScrollableHeight, null, disableAnimation: true);
        });
}
