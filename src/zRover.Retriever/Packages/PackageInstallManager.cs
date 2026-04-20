using Microsoft.Extensions.Logging;

namespace zRover.Retriever.Packages;

/// <summary>
/// Controls whether MCP clients are allowed to install or uninstall MSIX packages
/// on this machine. Disabled by default; must be explicitly enabled via the UI
/// toggle or a <c>zrover://enable-package-install</c> protocol activation.
///
/// This gate covers <c>install_package</c>, <c>uninstall_package</c>, and
/// <c>request_package_upload</c> — the destructive/write package operations.
/// Read-only operations (<c>list_installed_packages</c>, <c>get_package_info</c>,
/// <c>launch_app</c>, <c>stop_app</c>) are not gated.
///
/// Dev cert initialisation (which may show a UAC prompt to trust the cert) is
/// deferred to the first actual unsigned-package install rather than triggered on
/// every toggle / startup restore — see <see cref="LocalDevicePackageManager"/>.
/// </summary>
public sealed class PackageInstallManager
{
    private readonly ILogger<PackageInstallManager> _logger;

    public PackageInstallManager(ILogger<PackageInstallManager> logger)
    {
        _logger   = logger;
    }

    /// <summary>Whether package install/uninstall operations are currently permitted.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>Raised when <see cref="IsEnabled"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Allows MCP clients to install and uninstall packages.
    /// Cheap and side-effect-free: the dev cert (and any UAC trust prompt) is only
    /// initialised lazily when an unsigned package is actually installed.
    /// </summary>
    public Task EnableAsync(CancellationToken ct = default)
    {
        if (IsEnabled) return Task.CompletedTask;

        IsEnabled = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>Prevents MCP clients from installing or uninstalling packages.</summary>
    public void Disable()
    {
        if (!IsEnabled) return;
        IsEnabled = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
