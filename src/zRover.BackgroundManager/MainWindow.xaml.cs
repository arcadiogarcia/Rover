using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using zRover.BackgroundManager.Sessions;
using zRover.Core.Sessions;

namespace zRover.BackgroundManager;

public sealed partial class MainWindow : Window
{
    private readonly SessionRegistry _registry;
    private readonly IConfiguration _config;
    private readonly DispatcherQueue _dispatcherQueue;

    public MainWindow()
    {
        InitializeComponent();
        Title = "zRover Background Manager";

        var services = App.Services!;
        _registry = services.GetRequiredService<SessionRegistry>();
        _config = services.GetRequiredService<IConfiguration>();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _registry.SessionsChanged += OnSessionsChanged;
        _registry.ActiveSessionChanged += OnActiveSessionChanged;
        Closed += OnClosed;

        RefreshState();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _registry.SessionsChanged -= OnSessionsChanged;
        _registry.ActiveSessionChanged -= OnActiveSessionChanged;
    }

    private void OnSessionsChanged(object? sender, EventArgs e) =>
        _dispatcherQueue.TryEnqueue(RefreshState);

    private void OnActiveSessionChanged(object? sender, ActiveSessionChangedEventArgs e) =>
        _dispatcherQueue.TryEnqueue(RefreshState);

    private void RefreshState()
    {
        var url = _config["Urls"] ?? "http://localhost:5200";
        ListeningUrlText.Text = $"URL: {url}";

        var sessions = _registry.Sessions;
        var activeId = _registry.ActiveSession?.SessionId;

        var items = sessions.Select(s => new SessionViewModel
        {
            SessionId = s.SessionId,
            DisplayName = s.Identity.DisplayName,
            McpUrl = s.McpUrl,
            IsConnected = s.IsConnected,
            IsActive = s.SessionId == activeId
        }).ToList();

        SessionsList.ItemsSource = items;
        NoSessionsText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionsList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

public class SessionViewModel
{
    public string SessionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string McpUrl { get; set; } = "";
    public bool IsConnected { get; set; }
    public bool IsActive { get; set; }

    public string Details => $"{SessionId} • {McpUrl}";

    public SolidColorBrush StatusColor => IsConnected
        ? new SolidColorBrush(Colors.Green)
        : new SolidColorBrush(Colors.Red);

    public Visibility ActiveVisibility => IsActive
        ? Visibility.Visible
        : Visibility.Collapsed;
}
