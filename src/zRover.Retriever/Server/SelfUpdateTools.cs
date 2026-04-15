using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel;
using zRover.Core;
using zRover.Retriever.Sessions;

namespace zRover.Retriever.Server;

/// <summary>
/// Registers the <c>update_retriever</c> MCP tool, which fetches the latest
/// release from GitHub, downloads the MSIX, installs it via PowerShell
/// (which force-closes all running instances), and relaunches the app.
///
/// Supports federation: pass a <c>deviceId</c> to update any device reachable
/// via the remote-manager chain, including multi-hop paths ("a1b2:c3d4").
///
/// Prerequisites (satisfied by the normal first-run install):
/// <list type="bullet">
///   <item>The release signing certificate is already trusted in
///         <c>Cert:\LocalMachine\TrustedPeople</c>.</item>
/// </list>
/// </summary>
public static class SelfUpdateTools
{
    private const string GitHubApiLatest =
        "https://api.github.com/repos/arcadiogarcia/zRover/releases/latest";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Register(
        IMcpToolRegistry registry,
        RemoteManagerRegistry remoteManagers,
        ILogger logger)
    {
        registry.RegisterTool(
            "update_retriever",
            "Checks GitHub for the latest zRover Retriever release and, if a newer version is " +
            "available, downloads and installs it on the target device then restarts its Retriever. " +
            "Supports federation: pass a deviceId (from list_devices) to update a remote device, " +
            "including multi-hop paths (e.g. 'a1b2:c3d4'). Omit deviceId to update the local machine. " +
            "The signing certificate must already be trusted on the target machine. " +
            "Returns immediately with the update status; the Retriever process will exit and " +
            "relaunch automatically within a few seconds.",
            """
            {
              "type": "object",
              "properties": {
                "deviceId": {
                  "type": "string",
                  "description": "Target device ID from list_devices. Omit or pass null to update the local machine."
                }
              }
            }
            """,
            async argsJson =>
            {
                // ── Route to remote device if deviceId is specified ────────────────────
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("deviceId", out var devEl) &&
                    devEl.ValueKind == JsonValueKind.String)
                {
                    var deviceId = devEl.GetString();
                    if (!string.IsNullOrEmpty(deviceId) &&
                        !deviceId.Equals("local", StringComparison.OrdinalIgnoreCase))
                    {
                        // Strip deviceId before forwarding so the remote hop acts locally
                        var fwdArgs = BuildArgsWithoutDeviceId(root);
                        return await remoteManagers.RouteDeviceToolAsync(
                            deviceId, "update_retriever", fwdArgs, logger);
                    }
                }

                // ── 1. Read current installed version ─────────────────────────────────
                var currentPkg    = Package.Current;
                var currentPkgVer = currentPkg.Id.Version;
                var currentVer    = new Version(
                    currentPkgVer.Major, currentPkgVer.Minor,
                    currentPkgVer.Build, currentPkgVer.Revision);
                var familyName    = currentPkg.Id.FamilyName;
                var aumid         = $"{familyName}!App";

                logger.LogInformation("Self-update check: current version {Version}", currentVer);

                // ── 2. Fetch latest GitHub release ─────────────────────────────────────
                GitHubRelease release;
                try
                {
                    using var hc = BuildHttpClient();
                    release = await hc.GetFromJsonAsync<GitHubRelease>(
                        GitHubApiLatest,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
                        throw new InvalidDataException("Empty response from GitHub API");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to fetch latest release from GitHub");
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error   = "GITHUB_API_FAILED",
                        message = $"Could not reach GitHub releases API: {ex.Message}",
                    });
                }

                var tagName    = release.TagName ?? "";
                var latestVerStr = tagName.TrimStart('v');

                if (!Version.TryParse(latestVerStr, out var latestVer))
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error   = "INVALID_RELEASE_VERSION",
                        message = $"Could not parse version from tag '{tagName}'.",
                    });
                }

                logger.LogInformation("Latest GitHub release: {Version}", latestVer);

                // ── 3. Version comparison ──────────────────────────────────────────────
                if (latestVer <= currentVer)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success        = true,
                        alreadyCurrent = true,
                        message        = $"Already on the latest version ({currentVer}).",
                        currentVersion = currentVer.ToString(),
                        latestVersion  = latestVer.ToString(),
                    });
                }

                // ── 4. Locate MSIX asset for this architecture ─────────────────────────
                var arch      = GetArchitectureLabel();
                var assetName = $"zRover.Retriever_{latestVerStr}_{arch}.msix";
                var asset     = release.Assets?.FirstOrDefault(
                    a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));

                if (asset is null)
                {
                    // Fallback: any .msix asset in the release
                    asset = release.Assets?.FirstOrDefault(
                        a => a.Name?.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) == true);
                }

                if (asset is null || string.IsNullOrEmpty(asset.BrowserDownloadUrl))
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error   = "ASSET_NOT_FOUND",
                        message = $"No MSIX asset found in release '{tagName}'. " +
                                  $"Expected '{assetName}'. " +
                                  $"Available assets: {string.Join(", ", release.Assets?.Select(a => a.Name) ?? [])}",
                    });
                }

                // ── 5. Download to temp ────────────────────────────────────────────────
                var tempPath = Path.Combine(Path.GetTempPath(), assetName);
                logger.LogInformation(
                    "Downloading {Asset} from {Url} → {TempPath}",
                    asset.Name, asset.BrowserDownloadUrl, tempPath);

                try
                {
                    using var hc = BuildHttpClient();
                    using var response = await hc.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    await using var downloadStream = await response.Content.ReadAsStreamAsync();
                    await using var fileStream     = File.Create(tempPath);
                    await downloadStream.CopyToAsync(fileStream);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to download MSIX from {Url}", asset.BrowserDownloadUrl);
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error   = "DOWNLOAD_FAILED",
                        message = $"Failed to download MSIX: {ex.Message}",
                    });
                }

                // ── 6. Write a helper PowerShell script that installs and relaunches ────
                // We write the script to temp and execute it detached.  The script:
                //   a. Waits briefly so the MCP response is flushed back to the caller.
                //   b. Installs the MSIX (Add-AppxPackage -ForceApplicationShutdown
                //      terminates all running instances of the package, including us).
                //   c. Launches the newly-installed version via its shell:AppsFolder URI.
                //   d. Cleans up the downloaded MSIX.
                var escapedMsix   = tempPath.Replace("'", "''");
                var scriptContent = $"""
                    Start-Sleep -Seconds 3
                    Add-AppxPackage -Path '{escapedMsix}' -ForceApplicationShutdown
                    Start-Process "shell:AppsFolder\{aumid}"
                    Remove-Item '{escapedMsix}' -ErrorAction SilentlyContinue
                    """;

                var scriptPath = Path.Combine(Path.GetTempPath(), "zrover-selfupdate.ps1");
                await File.WriteAllTextAsync(scriptPath, scriptContent);

                logger.LogInformation(
                    "Launching update script for v{Version} (aumid: {Aumid})", latestVer, aumid);

                Process.Start(new ProcessStartInfo
                {
                    FileName        = "powershell.exe",
                    Arguments       = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                    UseShellExecute = true,
                    WindowStyle     = ProcessWindowStyle.Hidden,
                });

                // ── 7. Return success and schedule exit ────────────────────────────────
                // ForceApplicationShutdown in the PS script will terminate us anyway, but
                // we also schedule a clean exit here so we do not leave a zombie process
                // if the package was already unregistered before the script fires.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    logger.LogInformation("Self-update: exiting to allow new version to start");
                    Environment.Exit(0);
                });

                return JsonSerializer.Serialize(new
                {
                    success        = true,
                    alreadyCurrent = false,
                    message        = $"Update from v{currentVer} to v{latestVer} in progress. The Retriever will close and relaunch automatically.",
                    currentVersion = currentVer.ToString(),
                    newVersion     = latestVer.ToString(),
                    assetName      = asset.Name,
                }, JsonOpts);
            });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildArgsWithoutDeviceId(JsonElement root)
    {
        var args = new Dictionary<string, object?>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals("deviceId", StringComparison.OrdinalIgnoreCase)) continue;
            args[prop.Name] = prop.Value.Clone();
        }
        return JsonSerializer.Serialize(args);
    }

    private static HttpClient BuildHttpClient()
    {
        var hc = new HttpClient();
        hc.DefaultRequestHeaders.Add("User-Agent", "zRover.Retriever/self-update");
        hc.Timeout = TimeSpan.FromMinutes(10); // allow time for large MSIX downloads
        return hc;
    }

    private static string GetArchitectureLabel() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86   => "x86",
            _                  => "x64",
        };

    // ── GitHub release DTOs ───────────────────────────────────────────────────

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
