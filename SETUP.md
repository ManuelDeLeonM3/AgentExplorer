# Setup guide

This guide covers everything you need to run **Agent Explorer**: the Azure resources, the models, Entra ID sign-in and your local machine.

---

## 1. Local prerequisites

| Requirement | Notes |
|---|---|
| Windows 10 1809 (build 17763) or later | Windows 11 recommended (Mica backdrop). |
| **Developer Mode** turned on | *Settings → System → For developers → Developer Mode*. You need this to deploy the unsigned MSIX package when you press F5. |
| Visual Studio 2026 (or 2022 17.14+) | Workloads: **.NET desktop development** and the **WinUI application development** component. |
| .NET 10 SDK | Visual Studio installs it. Check with `dotnet --list-sdks`. |
| Azure CLI (optional) | Another way to sign in: `az login`. |

The NuGet packages restore automatically: `Microsoft.WindowsAppSDK` 1.8, `OpenAI`, `Azure.Identity`.

---

## 2. Create an Azure account (free trial)

1. Go to <https://azure.microsoft.com/free> and create a free account. It includes credit for the first 30 days.
2. Sign in to the [Azure portal](https://portal.azure.com).

> Free trial subscriptions have low default quotas, and some models may not be offered to them. If a model is missing when you deploy, try another region or [upgrade to pay-as-you-go](https://learn.microsoft.com/azure/cost-management-billing/manage/upgrade-azure-subscription). Upgrading keeps your remaining free credit. This demo uses very few tokens.

---

## 3. Create an Azure OpenAI resource

1. In the portal, choose **Create a resource**, search for **Azure OpenAI**, and click **Create**.
   (An **Azure AI Foundry** resource works too. It exposes the same OpenAI endpoint.)
2. Pick your subscription, a new resource group (e.g. `rg-agent-explorer`), and a **region where the GPT-5 family is available**, such as **East US 2** or **Sweden Central**. See the [model availability table](https://learn.microsoft.com/azure/ai-foundry/openai/concepts/models).
3. Enter a unique name (e.g. `agent-explorer-<yourname>`), choose the **Standard S0** pricing tier, and create the resource.
4. When the resource is ready, open it and copy the **Endpoint** from *Resource Management → Keys and Endpoint*, for example:
   `https://agent-explorer-alex.openai.azure.com/`

You don't need the keys. The app signs in with Entra ID.

---

## 4. Deploy the three models

1. From the resource's **Overview** page, click **Go to Azure AI Foundry portal**.
2. Open **Deployments → Deploy model → Deploy base model** and deploy each model below.
   **Keep the deployment name identical to the name shown here**, because these are the app's defaults:

   | Display name in the app | Model | Deployment name |
   |---|---|---|
   | GPT-5 Nano | `gpt-5-nano` | `gpt-5-nano` |
   | GPT-5 Mini | `gpt-5-mini` | `gpt-5-mini` |
   | GPT-5 | `gpt-5` | `gpt-5` |

3. **Global Standard** deployment type works well. A tokens-per-minute limit of 20K–50K is plenty.

> **Note:** At the time of writing, the full `gpt-5` model may require [registration / limited access](https://aka.ms/oai/gpt5access). `gpt-5-mini` and `gpt-5-nano` don't. If you can't deploy `gpt-5`, use the other two and remove it from the model list in the app's Settings.

You can use different names or add other models (e.g. `gpt-4.1-mini`) later in the app's **Settings** (see step 7).

---

## 5. Grant your account access (Entra ID / RBAC)

Entra ID sign-in needs a data-plane role on the resource. Being the subscription *Owner* is **not** enough.

1. In the portal, open your Azure OpenAI resource and select **Access control (IAM) → Add → Add role assignment**.
2. Select the role **Cognitive Services OpenAI User** and click **Next**.
3. Choose **User, group, or service principal → Select members**, pick your own account, then **Review + assign**.

It can take up to ~5 minutes to apply. If you get `401`/`403` errors in the app, wait a moment and try again.

Azure CLI alternative:

```powershell
az role assignment create `
  --assignee "<your-email>" `
  --role "Cognitive Services OpenAI User" `
  --scope "/subscriptions/<sub-id>/resourceGroups/rg-agent-explorer/providers/Microsoft.CognitiveServices/accounts/<resource-name>"
```

---

## 6. Sign in (single sign-on)

The app uses `DefaultAzureCredential`, which tries these sign-ins in order and uses the first one that works:

1. Environment variables (service principal). Usually not set.
2. **Visual Studio**: *Tools → Options → Azure Service Authentication → Account Selection*. Pick the account from step 5.
3. **Azure CLI**: run `az login` (add `--tenant <tenant-id>` if you have several tenants).
4. Azure PowerShell / Azure Developer CLI (`azd auth login`).
5. **Interactive browser**: if nothing above works, a browser window opens so you can sign in with your Entra ID account.

Click **Sign in** at the top-right of the app to sign in, or just send a message. The app signs in automatically on first use.

If your account belongs to several tenants, put the tenant ID of the Azure OpenAI resource in **Settings → Tenant ID**.

---

## 7. Configure the app

1. Open `AgentExplorer.slnx` in Visual Studio.
2. Select the **x64** (or **ARM64**) platform and press **F5**. Visual Studio builds, deploys the MSIX package and starts the app.
3. Click **Settings** (bottom of the navigation pane) and enter:
   - **Azure OpenAI endpoint**: the URL from step 3.
   - **Tenant ID**: optional.
   - **Models**: one per line, `Display name = deployment name`. Defaults:
	 ```
	 GPT-5 Nano = gpt-5-nano
	 GPT-5 Mini = gpt-5-mini
	 GPT-5 = gpt-5
	 ```
4. Save, pick a model from the **Model** drop-down in the title bar, and start at step 1.

Instead of typing the endpoint, you can set the `AZURE_OPENAI_ENDPOINT` environment variable (and optionally `AZURE_TENANT_ID`) before starting Visual Studio.

The app calls the Azure OpenAI **v1** API (`<endpoint>/openai/v1/`) with the official OpenAI .NET SDK, using a bearer token from `DefaultAzureCredential` for the `https://cognitiveservices.azure.com/.default` scope.

---

## 8. Other services and local data

| What | Where / how |
|---|---|
| Weather tool | [Open-Meteo](https://open-meteo.com/). Free, no key or sign-up needed. Needs internet access. |
| Memory tool (step 6) | JSON file `agent-memory.json` in the package's local folder: `%LOCALAPPDATA%\Packages\AgentExplorer.Demo_<id>\LocalState\`. Use **Clear memory** in the app to reset it. |
| Code file (step 7) | Kept in memory while the app runs. Use **Reset file** to restore it. |
| Settings | Packaged app local settings. They persist between runs. |

---

## 9. Troubleshooting

| Symptom | Fix |
|---|---|
| F5 fails with "developer license / sideloading" | Turn on **Developer Mode** (step 1). |
| "Packaged .NET applications … cannot be ProcessorArchitecture neutral" | Select **x64** or **ARM64** instead of *Any CPU*. |
| `Sign-in failed` / `CredentialUnavailableException` | Sign in to Visual Studio or run `az login`, then restart the app. |
| `401` / `403 PermissionDenied` | Assign **Cognitive Services OpenAI User** to your account (step 5) and wait a few minutes. Check the tenant ID. |
| `404 DeploymentNotFound` | The deployment name in Settings must exactly match the one in Azure AI Foundry. |
| `429 Too Many Requests` | The deployment's TPM quota is too low. Raise it in Azure AI Foundry → Deployments. |
| Weather tool errors | Check your internet connection. Open-Meteo may be temporarily unavailable. |
