# IINACT Copilot Instructions

## What this project is

IINACT is a [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FFXIV that runs the `FFXIV_ACT_Plugin` parser inside the game process and exposes combat data via a WebSocket server. It replaces ACT (Advanced Combat Tracker) with a modern, cross-platform .NET implementation.

## Build

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9) and a Dalamud installation. Clone with submodules:

```sh
git clone --recurse-submodules https://github.com/marzent/IINACT.git
cd IINACT
dotnet build
```

**Dalamud path resolution by OS:**
- **Windows:** `%APPDATA%\XIVLauncher\addon\Hooks\dev\`
- **macOS:** `~/Library/Application Support/XIV on Mac/dalamud/Hooks/dev/`
- **Linux:** set `DALAMUD_HOME` (e.g. `$HOME/.xlcore/dalamud/Hooks/dev`)

Release build (used by CI):
```sh
dotnet build -c release
```

There are no automated tests in this repository.

## Architecture

The solution has five projects wired together:

| Project | Role |
|---|---|
| `IINACT` | Dalamud plugin entry point (`Plugin.cs`). Owns `Configuration`, UI windows, commands, IPC providers, and wires everything together. |
| `NotACT` | ACT API shim (`Advanced_Combat_Tracker` namespace). Implements `FormActMain`, `ActGlobals`, and data types that `FFXIV_ACT_Plugin` expects from ACT, without any actual ACT dependency. |
| `OverlayPlugin.Core` | Port of OverlayPlugin for modern .NET. Hosts event sources, memory processors, network processors, a WebSocket server (`NetCoreServer`-based), and Dalamud IPC handlers. Uses `TinyIoC` for dependency injection (see `OverlayPlugin.Common/TinyIoC.cs`). |
| `OverlayPlugin.Common` | Shared interfaces (`IEventSource`, `IOverlay`, `ILogger`, etc.) and the `Registry`. |
| `FetchDependencies` | Downloads the correct `FFXIV_ACT_Plugin.dll` at runtime from `iinact.com` (global) or `cninact.diemoe.net` (Chinese). The DLL is **not** bundled in the repo. |

**Key data flow:**
1. `FfxivActPluginWrapper` initialises `FFXIV_ACT_Plugin` inside the game process, routing network packets via Machina/Unscrambler (no Deucalion / elevated privileges needed).
2. `FFXIV_ACT_Plugin` emits log lines into `FormActMain` (the `NotACT` shim).
3. `OverlayPlugin.Core` event sources (`MiniParseEventSource`, `FFXIVRequiredEventSource`, etc.) read from `FormActMain` / memory processors and broadcast JSON events.
4. `ServerController` (WebSocket) delivers those events to overlay clients.

**Initialisation is two-phased** inside `OverlayPlugin.Core/PluginMain.cs`:
- Phase 1: infrastructure, config, WebSocket server skeleton, overlay presets.
- Phase 2 (runs on `Task.Run`): FFXIV plugin integration, memory processors, event sources, overlays, IPC.

## Key conventions

### Dependency injection
`TinyIoCContainer` (in `OverlayPlugin.Common`) is the IoC container used throughout `OverlayPlugin.Core`. Register singletons with `container.Register(...)`. Lazy-loaded singletons for memory processors use the interface-registration pattern:
```csharp
_container.Register<ICombatantMemory, CombatantMemoryManager>();
```

### Dalamud plugin services
`Plugin.cs` receives all Dalamud services via constructor injection (the Dalamud service locator pattern). Add new services by adding a parameter to the `Plugin` constructor — do **not** use `Services.Get<T>()` static access.

### Configuration
`Configuration` implements `IPluginConfiguration` and is loaded/saved via `PluginInterface.GetPluginConfig()` / `PluginInterface.SavePluginConfig()`. Some config properties delegate directly to `ActGlobals.oFormActMain` (e.g. `WriteLogFile`, `DisableWritingPvpLogFile`).

### Namespace / project conventions
- `IINACT` project: namespace `IINACT`
- `NotACT` project: namespace `Advanced_Combat_Tracker` (intentional — matches the ACT public API)
- `OverlayPlugin.Core/Common`: namespace `RainbowMage.OverlayPlugin` (matches upstream OverlayPlugin)

### Windows targeting without Windows
All projects set `<EnableWindowsTargeting>true</EnableWindowsTargeting>` and target `net10.0-windows` so Windows Forms types (used by the `NotACT` shim) compile on Linux/macOS CI runners.

### `packages.lock.json`
All projects use `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`. After changing any `<PackageReference>`, regenerate the lock file:
```sh
dotnet restore --force-evaluate
```

### Chinese locale support
`DataManager.Language == "ChineseSimplified"` is a first-class path throughout: `FetchDependencies` downloads from a Chinese CDN, and `OpcodeManager` switches to `GameRegion.Chinese`.

### IPC surface
External plugins interact with IINACT via Dalamud IPC gates registered in `IpcProviders.cs`. The IPC version is tracked separately (`IpcVersion = 2.1.0`) from the plugin assembly version.

### External dependency
`external_dependencies/FFXIV_ACT_Plugin.dll` (and the `SDK/` subfolder) are pre-placed proprietary binaries referenced by `<Reference>` items. They are not downloaded by FetchDependencies at build time — only the runtime copy is fetched.
