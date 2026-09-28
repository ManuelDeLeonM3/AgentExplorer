using System.Text.Json;

namespace AgentExplorer.Services;

public sealed record ModelOption(string DisplayName, string Deployment);

/// <summary>User settings persisted in local application data.</summary>
public sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentExplorer", "settings.json");
    private static readonly Dictionary<string, string> Store = Load();

    public static IReadOnlyList<ModelOption> DefaultModels { get; } =
    [
        new("GPT-5 Nano", "gpt-5-nano"),
        new("GPT-5 Mini", "gpt-5-mini"),
        new("GPT-5", "gpt-5"),
    ];

    public string Endpoint
    {
        get => Get(nameof(Endpoint)) ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "";
        set => Set(nameof(Endpoint), value.Trim());
    }

    public string TenantId
    {
        get => Get(nameof(TenantId)) ?? Environment.GetEnvironmentVariable("AZURE_TENANT_ID") ?? "";
        set => Set(nameof(TenantId), value.Trim());
    }

    public string SelectedDeployment
    {
        get => Get(nameof(SelectedDeployment)) ?? "gpt-5-mini";
        set => Set(nameof(SelectedDeployment), value);
    }

    public IReadOnlyList<ModelOption> Models
    {
        get
        {
            var json = Get(nameof(Models));
            if (string.IsNullOrWhiteSpace(json)) return DefaultModels;
            try
            {
                var list = JsonSerializer.Deserialize<List<ModelOption>>(json);
                return list is { Count: > 0 } ? list : DefaultModels;
            }
            catch (JsonException)
            {
                return DefaultModels;
            }
        }
        set => Set(nameof(Models), JsonSerializer.Serialize(value));
    }

    public bool IsConfigured => Uri.TryCreate(Endpoint, UriKind.Absolute, out _);

    private static string? Get(string key) =>
        Store.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static Dictionary<string, string> Load()
    {
        if (!File.Exists(SettingsPath)) return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsPath)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Set(string key, string value)
    {
        Store[key] = value;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Store));
    }
}
