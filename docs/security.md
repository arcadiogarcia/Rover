# Security Considerations

zRover is a development and testing tool that, by design, gives external clients (AI agents, test harnesses, scripts) the ability to **see your screen**, **inject input**, **install software**, and **launch apps** on a Windows machine. Those capabilities are useful for closing the inner loop of agent-driven development, but they are also exactly the capabilities malware needs. This document explains the risks and how to deploy zRover in a way that keeps them contained.

> **TL;DR**: Treat any machine running zRover as exposed to whoever can reach its MCP endpoint. Run it only on test devices, only on trusted networks, and prefer `localhost`-only with auth tokens enabled.

## Table of Contents

- [Threat Model](#threat-model)
- [Risks by Component](#risks-by-component)
  - [In-app Library (`zRover.Uwp` / `zRover.WinUI`)](#in-app-library-zroveruwp--zroverwinui)
  - [Retriever Background Service](#retriever-background-service)
- [Recommended Deployment](#recommended-deployment)
- [Hardening Checklist](#hardening-checklist)

---

## Threat Model

zRover is designed for **trusted, local, development-time use**. The MCP endpoints it exposes are not hardened against a hostile network or a hostile MCP client. In particular, anyone who can reach the endpoint and (where applicable) present the bearer token can:

- Capture screenshots of the app or the visual tree, including any sensitive content currently on screen (credentials, tokens, personal data).
- Inject arbitrary mouse, touch, keyboard, pen, and gamepad input into the app.
- Read application diagnostic logs.
- Through the Retriever: install MSIX packages (including unsigned ones, after a one-time trust prompt), uninstall packages, launch and terminate apps, and forward all of the above to federated remote machines.

zRover does **not** attempt to defend against:

- A malicious user on the same machine.
- A malicious process running with equal or higher privileges.
- A compromised MCP client that has been given the auth token.
- Network attackers when external access is enabled without TLS in front of it.

## Risks by Component

### In-app Library (`zRover.Uwp` / `zRover.WinUI`)

The in-app library starts an HTTP MCP server inside (or alongside) your app process. By default it listens on `http://localhost:5100/mcp` and **requires no authentication**.

Concrete risks:

- **Local exposure.** Any process running as the same user can connect to `localhost:5100` and drive your app — capture its screen, type into it, dispatch app actions, etc.
- **No transport encryption.** The endpoint is plain HTTP. Do not put it behind a network listener without a TLS-terminating reverse proxy.
- **Shipping in production.** If the package is accidentally included in a release build, every install of your app will silently expose this endpoint. zRover is intended for **debug builds only** — see the integration guide for how to exclude it from release configurations.

Mitigations the library provides:

- **Bearer token auth.** Pass `requireAuthToken: true` and an `authToken` to `DebugHost.Start()` (or set the equivalent properties on `DebugHostOptions`) and configure the same token in your MCP client. See the [Integration Guide](integration-guide.md#configuration).
- **Localhost-only binding.** The library always binds the MCP listener to `localhost`, so it is not directly reachable from another machine. Reaching it from a remote MCP client requires either an explicit port forwarder/reverse proxy that you set up, or routing through the Retriever's federation listener.

### Retriever Background Service

The Retriever is significantly more powerful than the in-app library because it can install software and federate to other machines.

Concrete risks:

- **Unsigned package installation.** The Retriever can install MSIX packages that are not signed by a trusted publisher by automatically re-signing them with a per-machine development certificate (`allowUnsigned` / the automatic signing flow described in [Package Deployment](package-deployment.md#automatic-signing)). After the one-time trust prompt for that dev cert, _any_ package the Retriever signs will install without further prompts. An attacker who can reach the Retriever's package-management API on a machine where the dev cert is trusted can install arbitrary software as that user.
- **Package management gate.** Package installation tools are disabled by default and must be enabled per-device via the Retriever UI or `zrover://enable-package-install`. **Leave this off** unless you are actively deploying.
- **Federation widens the blast radius.** When external access is enabled (`zrover://enable-external`), the Retriever opens a network listener (default port 5201) protected by a bearer token. Any machine that holds a valid token becomes a fully-trusted MCP client of that Retriever, and through multi-hop federation can reach every other machine downstream. A leaked connection link is equivalent to handing over interactive control of every federated device.
- **Plain HTTP over the network.** The federation listener is not TLS-encrypted. On an untrusted network, both the bearer token and the in-flight tool calls (including screenshots) can be intercepted.
- **Connection links contain secrets.** The `zrover://` connection link encodes the bearer token. Treat it like a password: do not paste it into chat, issue trackers, screenshots, or shared documents.

## Recommended Deployment

The safest way to use zRover, in order from most to least restrictive:

1. **Dedicated test device, no network exposure.** Use zRover only on a physical or virtual machine you have set aside for testing. Keep external access **off**. Keep package management **off** unless you are actively deploying. Only `localhost` MCP clients (an agent running on the same machine) can reach it.
2. **Trusted local network with auth tokens.** If you must reach the device from another machine, enable external access only while you need it, treat the generated connection link as a secret, and disable external access when you are done. Restrict access to a network segment you control (e.g. a lab VLAN, not your corporate Wi-Fi or a coffee-shop network).
3. **Never on a production or daily-driver machine.** Do not run the Retriever on a machine that holds credentials, source code you cannot afford to lose, or access to production systems. Do not ship the in-app library in release builds of consumer or enterprise applications.

## Hardening Checklist

Before connecting an agent to zRover, confirm:

- [ ] The machine is a test device that does not hold production secrets, signed-in personal accounts, or sensitive data.
- [ ] The in-app library is included only in **debug** build configurations of your app.
- [ ] The in-app library is started with `requireAuthToken: true` and a strong, unique `authToken` (and the same token is configured in the MCP client).
- [ ] The MCP endpoint binding is `localhost` unless you have an explicit reason and a TLS-terminating proxy in front of it.
- [ ] Retriever **package management** is left disabled unless you are actively installing builds.
- [ ] Retriever **external access** is left disabled unless you are actively federating, and is turned off again afterwards.
- [ ] Federation `zrover://` connection links are not pasted into chat, screenshots, issue trackers, or version control.
- [ ] The Retriever's signing certificate is trusted only on machines that you control and that you are willing to install arbitrary unsigned MSIX packages on.
- [ ] Any host running zRover is on a network segment you trust (no public Wi-Fi, no untrusted shared networks).
