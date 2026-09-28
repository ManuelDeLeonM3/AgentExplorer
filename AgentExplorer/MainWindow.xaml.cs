using AgentExplorer.Models;
using AgentExplorer.Services;
using AgentExplorer.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentExplorer;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, ScenarioView> _views = [];
    private bool _loadingModels;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1560, 980));

        Nav.MenuItems.Add(new NavigationViewItemHeader { Content = "Learn step by step" });
        foreach (var scenario in Scenarios.All)
        {
            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = $"{scenario.Step}. {scenario.Title}",
                Tag = scenario.Key,
                Icon = new FontIcon { Glyph = scenario.Glyph },
            });
        }

        AppState.Auth.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateAccount);
        LoadModels();
        UpdateAccount();
        UpdateSetupInfo();
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First();
    }

    private void LoadModels()
    {
        _loadingModels = true;
        ModelPicker.Items.Clear();
        var selected = AppState.SelectedModel;
        foreach (var model in AppState.Settings.Models)
        {
            var item = new ComboBoxItem { Content = $"{model.DisplayName}  ·  {model.Deployment}", Tag = model };
            ModelPicker.Items.Add(item);
            if (model == selected) ModelPicker.SelectedItem = item;
        }
        _loadingModels = false;
    }

    private void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingModels && ModelPicker.SelectedItem is ComboBoxItem { Tag: ModelOption model })
        {
            AppState.SelectedModel = model;
        }
    }

    private void UpdateAccount()
    {
        var auth = AppState.Auth;
        AccountText.Text = auth.UserName ?? "Sign in";
        AccountPicture.DisplayName = auth.UserName ?? "";
        ToolTipService.SetToolTip(AccountButton, auth.IsSignedIn
            ? "Signed in with Entra ID. Click to sign out."
            : "Sign in with Entra ID (DefaultAzureCredential)");
    }

    private void UpdateSetupInfo() => SetupInfoBar.IsOpen = !AppState.Settings.IsConfigured;

    private async void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        var auth = AppState.Auth;
        if (auth.IsSignedIn)
        {
            auth.SignOut();
            return;
        }

        AccountButton.IsEnabled = false;
        AccountText.Text = "Signing in…";
        try
        {
            await auth.SignInAsync();
        }
        catch (Exception ex)
        {
            UpdateAccount();
            await new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Sign-in failed",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Text = "Sign in to Visual Studio (Tools > Options > Azure Service Authentication) or run 'az login', then try again. See SETUP.md.\n\n" + ex.Message,
                },
                CloseButtonText = "OK",
            }.ShowAsync();
        }
        finally
        {
            AccountButton.IsEnabled = true;
        }
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string key }) return;
        var scenario = Scenarios.All.FirstOrDefault(s => s.Key == key);
        if (scenario is null) return;

        // Views are cached so each screen keeps its conversation when you switch back and forth.
        if (!_views.TryGetValue(key, out var view))
        {
            view = new ScenarioView(scenario);
            _views[key] = view;
        }
        Nav.Content = view;
    }

    private async void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag as string == "settings")
        {
            await ShowSettingsAsync();
        }
    }

    private async void OpenSettings_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    public async Task ShowSettingsAsync()
    {
        var settings = AppState.Settings;
        var endpoint = new TextBox
        {
            Header = "Azure OpenAI endpoint",
            PlaceholderText = "https://<your-resource>.openai.azure.com/",
            Text = settings.Endpoint,
        };
        var tenant = new TextBox
        {
            Header = "Tenant ID (optional)",
            PlaceholderText = "Only needed if your account belongs to several tenants",
            Text = settings.TenantId,
        };
        var models = new TextBox
        {
            Header = "Models (one per line:  Display name = deployment name)",
            AcceptsReturn = true,
            Height = 120,
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["CodeFontFamily"],
            Text = string.Join("\r", settings.Models.Select(m => $"{m.DisplayName} = {m.Deployment}")),
        };
        var panel = new StackPanel { Spacing = 14, MinWidth = 460 };
        panel.Children.Add(endpoint);
        panel.Children.Add(tenant);
        panel.Children.Add(models);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Text = "Authentication uses your Entra ID account (DefaultAzureCredential), no API keys. Your account needs the 'Cognitive Services OpenAI User' role on the resource.",
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Settings",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var tenantChanged = tenant.Text.Trim() != settings.TenantId;
        settings.Endpoint = endpoint.Text;
        settings.TenantId = tenant.Text;

        var parsed = models.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.All(p => p.Length > 0))
            .Select(parts => parts.Length == 2 ? new ModelOption(parts[0], parts[1]) : new ModelOption(parts[0], parts[0]))
            .ToList();
        settings.Models = parsed.Count > 0 ? parsed : [.. AppSettings.DefaultModels];

        if (tenantChanged) AppState.Auth.SignOut();
        LoadModels();
        AppState.SelectedModel = AppState.SelectedModel;
        UpdateSetupInfo();
    }
}
