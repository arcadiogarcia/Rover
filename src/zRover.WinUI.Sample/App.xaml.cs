using System;
using Microsoft.UI.Xaml;

namespace zRover.WinUI.Sample
{
    sealed partial class App : Application
    {
        private Window? m_window;

        public App()
        {
            this.InitializeComponent();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();
            m_window.Activate();

#if DEBUG
            var mainPage = (m_window as MainWindow)?.MainPage;
            var actionableApp = mainPage as zRover.Core.IActionableApp;
            await zRover.WinUI.RoverMcp.StartAsync(
                m_window,
                "zRover.WinUI.Sample",
                actionableApp: actionableApp,
                managerUrl: "http://localhost:5200");
            zRover.WinUI.RoverMcp.Log("App", $"zRover WinUI MCP started");
            m_window.Closed += async (s, e) => await zRover.WinUI.RoverMcp.StopAsync();
#endif
        }
    }
}
