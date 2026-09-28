using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace AgentExplorer.Services;

/// <summary>Entra ID single sign-on through <see cref="DefaultAzureCredential"/>.</summary>
public sealed class AuthService(AppSettings settings)
{
    public const string Scope = "https://cognitiveservices.azure.com/.default";

    private TokenCredential? _credential;

    public event EventHandler? Changed;

    public string? UserName { get; private set; }

    public bool IsSignedIn => UserName is not null;

    public TokenCredential Credential => _credential ??= CreateCredential();

    private TokenCredential CreateCredential()
    {
        var options = new DefaultAzureCredentialOptions
        {
            // Try developer sign-ins first (Visual Studio, Azure CLI, azd, PowerShell),
            // then fall back to an interactive browser sign-in.
            ExcludeInteractiveBrowserCredential = false,
            ExcludeManagedIdentityCredential = true,
            ExcludeWorkloadIdentityCredential = true,
        };
        if (!string.IsNullOrWhiteSpace(settings.TenantId))
        {
            options.TenantId = settings.TenantId;
        }
        return new DefaultAzureCredential(options);
    }

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        var token = await Credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken);
        UserName = ReadUserName(token.Token) ?? "Signed in";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task EnsureSignedInAsync(CancellationToken cancellationToken) =>
        IsSignedIn ? Task.CompletedTask : SignInAsync(cancellationToken);

    public void SignOut()
    {
        _credential = null;
        UserName = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string? ReadUserName(string jwt)
    {
        try
        {
            var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            foreach (var claim in new[] { "name", "preferred_username", "upn", "unique_name" })
            {
                if (doc.RootElement.TryGetProperty(claim, out var value) && value.GetString() is { Length: > 0 } s)
                {
                    return s;
                }
            }
        }
        catch (Exception)
        {
        }
        return null;
    }
}
