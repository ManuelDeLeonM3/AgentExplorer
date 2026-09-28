using System.ClientModel.Primitives;
using AgentExplorer.Tools;
using OpenAI;
using OpenAI.Chat;

namespace AgentExplorer.Services;

/// <summary>Global, app-wide state shared by every screen.</summary>
public static class AppState
{
    public static AppSettings Settings { get; } = new();

    public static AuthService Auth { get; } = new(Settings);

    public static CodeDocument CodeDocument { get; } = new();

    public static MemoryStore Memory { get; } = new();

    public static MainWindow? Window { get; set; }

    public static event EventHandler? ModelChanged;

    public static ModelOption SelectedModel
    {
        get
        {
            var models = Settings.Models;
            return models.FirstOrDefault(m => m.Deployment == Settings.SelectedDeployment) ?? models[0];
        }
        set
        {
            Settings.SelectedDeployment = value.Deployment;
            ModelChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Creates an OpenAI SDK chat client that talks to the Azure OpenAI "v1" endpoint
    /// using an Entra ID bearer token (no API keys).
    /// </summary>
    public static ChatClient CreateChatClient(string deployment)
    {
        var endpoint = Settings.Endpoint.TrimEnd('/');
        if (!endpoint.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
        {
            endpoint += "/openai/v1";
        }

        var policy = new BearerTokenPolicy(Auth.Credential, AuthService.Scope);
        // Entra ID token auth for the Azure OpenAI v1 API is marked experimental in the OpenAI SDK.
#pragma warning disable OPENAI001
        var client = new OpenAIClient(policy, new OpenAIClientOptions { Endpoint = new Uri(endpoint + "/") });
#pragma warning restore OPENAI001
        return client.GetChatClient(deployment);
    }
}
