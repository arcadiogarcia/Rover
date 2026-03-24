# Rover — MCP In-App Debug Host for UWP

Rover is a [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) debug host that runs inside a UWP application, exposing screenshot capture and input injection as MCP tools. External clients (AI agents, test harnesses, etc.) connect over HTTP and can remotely observe and interact with the running app.

## Install

> **Prerequisite:** The Rover UWP app must be deployed and running on your machine (see [Building](#building) below). The MCP server listens on `http://localhost:5100/mcp`.

[<img src="https://img.shields.io/badge/VS_Code-VS_Code?style=flat-square&label=Install%20Server&color=0098FF" alt="Install in VS Code">](https://vscode.dev/redirect/mcp/install?name=rover&config=%7B%22url%22%3A%22http%3A%2F%2Flocalhost%3A5100%2Fmcp%22%7D) [<img src="https://img.shields.io/badge/VS_Code_Insiders-VS_Code_Insiders?style=flat-square&label=Install%20Server&color=24bfa5" alt="Install in VS Code Insiders">](https://insiders.vscode.dev/redirect/mcp/install?name=rover&config=%7B%22url%22%3A%22http%3A%2F%2Flocalhost%3A5100%2Fmcp%22%7D) [<img src="https://cursor.com/deeplink/mcp-install-dark.svg" alt="Install in Cursor">](https://cursor.com/en/install-mcp?name=rover&config=eyJ1cmwiOiJodHRwOi8vbG9jYWxob3N0OjUxMDAvbWNwIn0%3D) [<img src="https://img.shields.io/badge/Visual_Studio-Install-C16FDE?logo=visualstudio&logoColor=white" alt="Install in Visual Studio">](https://vs-open.link/mcp-install?%7B%22name%22%3A%22rover%22%2C%22url%22%3A%22http%3A%2F%2Flocalhost%3A5100%2Fmcp%22%7D)

**Or install manually:**

<details>
<summary>VS Code / Copilot</summary>

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "rover": {
      "type": "http",
      "url": "http://localhost:5100/mcp"
    }
  }
}
```

Or via CLI:

```bash
code --add-mcp '{"name":"rover","url":"http://localhost:5100/mcp"}'
```

</details>

<details>
<summary>Claude Desktop</summary>

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "rover": {
      "url": "http://localhost:5100/mcp"
    }
  }
}
```

</details>

<details>
<summary>Claude Code</summary>

```bash
claude mcp add rover --transport http --url http://localhost:5100/mcp
```

</details>

<details>
<summary>Cursor</summary>

Go to `Cursor Settings` → `MCP` → `New MCP Server`, then add:

```json
{
  "mcpServers": {
    "rover": {
      "url": "http://localhost:5100/mcp"
    }
  }
}
```

</details>

<details>
<summary>Windsurf</summary>

Add to your MCP config following the <a href="https://docs.windsurf.com/windsurf/cascade/mcp#mcp-config-json">configure MCP guide</a>:

```json
{
  "mcpServers": {
    "rover": {
      "serverUrl": "http://localhost:5100/mcp"
    }
  }
}
```

</details>

<details>
<summary>Generic MCP client (C#)</summary>

```csharp
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://localhost:5100/mcp")
});
var client = await McpClient.CreateAsync(transport);
var tools = await client.ListToolsAsync();
```

</details>

## Architecture

```
MCP Client (tests, AI agents, etc.)
    │
    │  HTTP Streamable MCP (port 5100)
    ▼
┌──────────────────────────────────┐
│  Rover.FullTrust.McpServer       │  .NET 8 console app
│  ASP.NET Core  MapMcp("/mcp")    │  Runs as FullTrust process
│                                  │
│  AppServiceConnection IPC        │
└──────────┬───────────────────────┘
           │  ValueSet messages
           │  (ping, list_tools, invoke_tool)
           ▼
┌──────────────────────────────────┐
│  Rover.Uwp.Sample               │  UWP AppContainer
│  ├─ App.xaml.cs                  │  In-process AppService handler
│  │  OnBackgroundActivated()      │
│  │                               │
│  ├─ DebugHost / DebugHostRunner  │  Orchestrates capabilities
│  │                               │
│  ├─ ToolRegistry (singleton)     │  Thread-safe tool lookup
│  │                               │
│  └─ Capabilities                 │
│     ├─ ScreenshotCapability      │  RenderTargetBitmap → PNG
│     └─ InputInjectionCapability  │  InputInjector or XAML automation
└──────────────────────────────────┘
```

### Data flow for a tool call

1. Client sends MCP `tools/call` via HTTP to `:5100/mcp`
2. FullTrust server receives it, looks up the proxy tool, sends an AppService IPC message
3. UWP app's `OnBackgroundActivated` dispatches to `ToolRegistry`
4. The capability handler runs (e.g. captures screenshot on UI thread)
5. JSON result flows back: capability → AppService → FullTrust → MCP HTTP response

## Projects

| Project | Target | Purpose |
|---|---|---|
| **Rover.Core** | netstandard2.0 | Interfaces (`IDebugCapability`, `IMcpToolRegistry`, `IToolBackend`), DTOs, coordinate types |
| **Rover.Mcp** | netstandard2.0 | MCP SDK adapter — bridges `IMcpToolRegistry` to `McpServerTool` via `DelegateMcpServerTool` |
| **Rover.Uwp** | UAP 10.0.19041 | UWP class library — debug host, capabilities, AppService handler, coordinate resolver |
| **Rover.Uwp.Sample** | UAP 10.0.19041 | Sample UWP app with Color Picker test UI for E2E testing |
| **Rover.FullTrust.McpServer** | net8.0-windows | Out-of-process MCP HTTP server, bridges to UWP via AppService IPC |
| **Rover.Mcp.IntegrationTests** | net8.0 | 34 xUnit tests (unit + E2E) |

## MCP Tools

| Tool | Description |
|---|---|
| `capture_current_view` | Renders the XAML visual tree to a PNG file. Returns `{ success, filePath, width, height }` |
| `inject_tap` | Taps at coordinates. Params: `x`, `y`, `coordinateSpace` (normalized/absolute/client), `device` |
| `inject_drag_path` | Drags along a path. Params: `points[]`, `durationMs`, `coordinateSpace`, `device` |

### Coordinate spaces

- **normalized** (default) — `0.0` to `1.0` relative to the app window
- **absolute** — screen pixels
- **client** — window-relative pixels (same as absolute for full-screen UWP)

### Input injection fallback

`InputInjector.TryCreate()` returns null on some Windows configurations even with the `inputInjectionBrokered` capability. When unavailable, Rover falls back to XAML automation:

- **Taps**: `VisualTreeHelper.FindElementsInHostCoordinates()` → `IInvokeProvider.Invoke()` for buttons
- **Drags**: Finds `Slider` at start point → `IRangeValueProvider.SetValue()` based on drag endpoint

## Prerequisites

- **Windows 10/11** (Desktop)
- **Visual Studio 2022** with the UWP workload
- **.NET 8 SDK**
- **Developer Mode** enabled in Windows Settings → Privacy & Security → For Developers

## Building

All commands assume the working directory is the repository root.

### 1. Publish the FullTrust MCP server

```powershell
dotnet publish src\Rover.FullTrust.McpServer\Rover.FullTrust.McpServer.csproj `
    -c Debug -r win-x64 --self-contained true /p:PublishSingleFile=true
```

This produces a single exe at:
`src\Rover.FullTrust.McpServer\bin\Debug\net8.0-windows10.0.19041.0\win-x64\publish\Rover.FullTrust.McpServer.exe`

The UWP sample project automatically picks it up and includes it in the AppX package as `FullTrust\Rover.FullTrust.McpServer.exe`.

### 2. Build and deploy the UWP app

```powershell
# Rebuild (compiles Rover.Uwp + Rover.Uwp.Sample + bundles FullTrust exe)
& "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe" `
    src\Rover.sln /Rebuild "Debug|x64" /Project "Rover.Uwp.Sample\Rover.Uwp.Sample.csproj"

# Deploy (registers the AppX package for sideloading)
& "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe" `
    src\Rover.sln /Deploy "Debug|x64" /Project "Rover.Uwp.Sample\Rover.Uwp.Sample.csproj"
```

> **Important:** Always use `devenv /Deploy` for deployment. Manual `Add-AppxPackage -Register` with file copying to the AppX directory is unreliable because the `bin\x64\Debug\AppX` subdirectory may contain stale files.

### 3. Launch the app

```powershell
Start-Process "shell:AppsFolder\Rover.Uwp.Sample_xaf3bmhg52ma0!App"
```

The app starts the debug host and launches the FullTrust MCP server automatically. Wait a few seconds, then verify:

```powershell
Test-NetConnection -ComputerName localhost -Port 5100 | Select-Object TcpTestSucceeded
```

## Running Tests

### Unit tests only (no app needed)

```powershell
dotnet test src\Rover.Mcp.IntegrationTests\Rover.Mcp.IntegrationTests.csproj `
    --filter "McpServerToolTests|McpToolRegistryAdapterTests|DelegateMcpServerToolInvocationTests"
```

Runs 14 tests that exercise the MCP adapter in isolation using in-memory pipes. No UWP app required.

### E2E tests only (app must be running on port 5100)

```powershell
dotnet test src\Rover.Mcp.IntegrationTests\Rover.Mcp.IntegrationTests.csproj `
    --filter "EndToEndPipelineTests|ColorPickerE2ETests"
```

Runs 20 tests that connect to the live MCP server. Requires the UWP app to be deployed, launched, and listening on port 5100.

### All tests

```powershell
dotnet test src\Rover.Mcp.IntegrationTests\Rover.Mcp.IntegrationTests.csproj
```

Runs all 34 tests. The E2E tests will fail if the app isn't running.

### Full workflow (build + deploy + launch + test)

```powershell
# 1. Publish FullTrust server
dotnet publish src\Rover.FullTrust.McpServer\Rover.FullTrust.McpServer.csproj `
    -c Debug -r win-x64 --self-contained true /p:PublishSingleFile=true

# 2. Rebuild UWP app
& "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe" `
    src\Rover.sln /Rebuild "Debug|x64" /Project "Rover.Uwp.Sample\Rover.Uwp.Sample.csproj"

# 3. Deploy
& "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe" `
    src\Rover.sln /Deploy "Debug|x64" /Project "Rover.Uwp.Sample\Rover.Uwp.Sample.csproj"

# 4. Launch app and wait for MCP server
Start-Process "shell:AppsFolder\Rover.Uwp.Sample_xaf3bmhg52ma0!App"
Start-Sleep -Seconds 5

# 5. Run all tests
dotnet test src\Rover.Mcp.IntegrationTests\Rover.Mcp.IntegrationTests.csproj
```

### Environment variable

The E2E tests default to `http://localhost:5100/mcp`. Override with:

```powershell
$env:ROVER_MCP_ENDPOINT = "http://localhost:5100/mcp"
```

## Test Inventory (34 tests)

### McpServerToolTests (8 unit tests)
- `ServerInfo_ReturnsCorrectName`
- `ListTools_ReturnsAllRegisteredTools`
- `ListTools_EachToolHasDescriptionAndSchema`
- `CallTool_Echo_ReturnsMessage`
- `CallTool_AddNumbers_ComputesCorrectSum`
- `CallTool_GetStatus_WorksWithNoArguments`
- `CallTool_FailingTool_ReturnsError`
- `Ping_ServerResponds`

### McpToolRegistryAdapterTests (4 unit tests)
- `RegisterTool_AddsToToolsCollection`
- `RegisterTool_ProtocolToolHasCorrectMetadata`
- `RegisterMultipleTools_AllPresent`
- `ImplementsIMcpToolRegistry`

### DelegateMcpServerToolInvocationTests (2 unit tests)
- `CallTool_PassesSerializedArguments`
- `CallTool_WithEmptyArgs_PassesEmptyObject`

### EndToEndPipelineTests (10 E2E tests)
- `Server_Responds_WithCorrectInfo`
- `Server_Ping_Succeeds`
- `ListTools_ReturnsRegisteredUwpTools`
- `ListTools_InjectTap_HasCorrectMetadata`
- `ListTools_CaptureCurrentView_HasSchema`
- `ListTools_InjectDragPath_HasPointsSchema`
- `CaptureCurrentView_ReturnsResult`
- `InjectTap_WithNormalizedCoordinates_Succeeds`
- `InjectDragPath_WithTwoPoints_Succeeds`
- `NonexistentTool_ReturnsError`

### ColorPickerE2ETests (10 E2E tests)
- `TapRedButton_PreviewBecomesRed`
- `TapGreenButton_PreviewBecomesGreen`
- `TapBlueButton_PreviewBecomesBlue`
- `TapYellowButton_PreviewBecomesYellow`
- `TapWhiteButton_PreviewBecomesWhite`
- `DragRedSlider_IncreasesRedComponent`
- `DragGreenSlider_IncreasesGreenComponent`
- `DragBlueSlider_IncreasesBlueComponent`
- `SequentialColorChanges_PreviewUpdatesEachTime`
- `Screenshot_HasReasonableDimensions`

## Key Implementation Details

### AppService IPC (in-process model)

The AppService is declared **without** an `EntryPoint` attribute in the manifest, which makes it run in-process. `App.OnBackgroundActivated` handles requests directly, sharing the `ToolRegistry` singleton with the capabilities. This avoids cross-process serialization of tool handlers.

### Screenshot capture

Uses `RenderTargetBitmap.RenderAsync(Window.Current.Content)` on the UI thread → `SoftwareBitmap` → `BitmapEncoder` (PNG). Files are saved to `LocalState\debug-artifacts\screenshots\`. The test process reads them with `FileShare.ReadWrite` to handle concurrent access.

### UWP deployment gotcha

`Add-AppxPackage -Register` from `bin\x64\Debug\AppxManifest.xml` registers a package, but the runtime may load files from a stale `AppX` subdirectory. Always use `devenv /Rebuild` then `devenv /Deploy` for reliable updates.

### Slider.ValueChanged during InitializeComponent

XAML fires `Slider.ValueChanged` during `InitializeComponent()` before other named elements are initialized. Subscribe to the event in code-behind **after** `InitializeComponent()` completes to avoid `NullReferenceException` crashes.

## What's Not Yet Implemented

- **stdio transport** — code exists but is unused; only HTTP is exercised
- **Mouse injection** — schema accepts `device: "mouse"` but only touch/automation is implemented
- **Automation fallback for more controls** — only `ButtonBase` and `Slider` are handled; no ToggleSwitch, ListView, TextBox, etc.
- **Non-UWP platforms** — no WinUI 3 or WPF adapter
- **MCP resources and prompts** — only tools are implemented
- **CI/CD pipeline** — tests require a locally deployed UWP app
- **Auth in production** — Bearer token support exists in `HttpMcpListener` but is unused
- **Host app integration** — no app-specific capabilities beyond the color picker sample exist yet
