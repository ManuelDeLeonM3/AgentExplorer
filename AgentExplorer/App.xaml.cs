using AgentExplorer.Services;
using Microsoft.UI.Xaml;

namespace AgentExplorer;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        AppState.Window = window;
        window.Activate();
    }
}
