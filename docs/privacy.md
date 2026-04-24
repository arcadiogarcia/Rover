# zRover Retriever — Privacy policy

_Last updated: 2026-04-23_

zRover Retriever is an open-source MCP (Model Context Protocol) server that
runs locally on the user's Windows device. This document describes what data
the application handles, where it goes, and the choices the user has.

## Summary

- **No analytics, no telemetry, no advertising IDs.** The Retriever does not
  send any usage data to the publisher or any third party.
- **No accounts.** There is no sign-in and no user identifier is created.
- **All data stays on the user's device** unless the user explicitly enables
  external access or connects to a remote Retriever.

## Data the app stores locally

The Retriever persists settings under
`%LOCALAPPDATA%\Packages\<package family>\LocalState\`:

| Data | Purpose | Location |
| --- | --- | --- |
| External-access toggle, port, bearer token | Remember the "Allow external connections" preference between launches | `settings.json` |
| Saved remote Retrievers (URL, optional bearer token, alias) | One-click reconnect to remote Retrievers the user has previously paired with | `settings.json` |
| "Allow package install" toggle | Remember whether MCP clients may install/uninstall MSIX packages | `settings.json` |
| Diagnostic logs | Local troubleshooting; rotated and capped in size | `logs/` |

The user can delete all of this data at any time by uninstalling the app or
clearing the package's local state from **Settings → Apps → zRover Retriever
→ Advanced options → Reset**.

## Network connections

The Retriever opens network sockets in the following situations:

1. **Localhost MCP server (always).** The Retriever listens on
   `http://localhost:5200` so MCP clients on the same machine (Copilot,
   Claude Desktop, custom tooling) can connect.
2. **External MCP server (opt-in).** When the user enables "Allow external
   connections," the Retriever also binds to a configurable port on all
   network interfaces and protects access with a bearer token shown in the
   UI and QR code.
3. **Remote Retrievers (user-initiated).** When the user pairs with another
   machine running zRover Retriever, the Retriever connects out to that
   machine's MCP URL using credentials the user provided.
4. **GitHub releases API (on demand).** The "Check for updates" MCP tool
   queries `https://api.github.com/repos/arcadiogarcia/zRover/releases/latest`.
   GitHub may log the request per its own privacy policy.

No other outbound connections are made.

## Data MCP clients receive

When a packaged WinUI app integrates the zRover SDK and the user's MCP
client invokes a tool, the tool may return:

- Screenshots of the running application window
- Visual-tree snapshots (control names, bounds, text content)
- Diagnostic log entries the app has emitted
- The output of explicit actions the client invokes

This data flows to whichever MCP client the user chose to connect. The
Retriever does not intermediate or store the contents of these responses
beyond what the OS already keeps in normal IPC buffers.

## Children

The Retriever is a developer / power-user tool and is not directed to
children under 13.

## Changes

Material changes to this policy will be announced in the GitHub release
notes and the file's `Last updated` date will be revised.

## Contact

Issues, questions, or data-handling concerns:
<https://github.com/arcadiogarcia/zRover/issues>
