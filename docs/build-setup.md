# Build setup

## Toolchain

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.403 (pinned in `global.json`, `rollForward: latestFeature`) | `winget install Microsoft.DotNet.SDK.10` |
| Windows App SDK | 2.6.5 (NuGet `Microsoft.WindowsAppSDK`) | Restored from NuGet; nothing to install |
| Windows SDK projection | `net10.0-windows10.0.26100.0` | Restored from NuGet |
| Node.js | 22.22.2+, 24.15+, or 26+ | Build and release scripts |
| NSIS | 3.10+ | Installer (not needed for app builds) |
| GnuPG | 2.4+ | Release signing only |

Visual Studio is not required. `dotnet build`, `dotnet publish`, and `dotnet test` work from a
plain terminal, including the XAML compiler.

Tests run on Microsoft.Testing.Platform. `global.json` opts in with
`"test": { "runner": "Microsoft.Testing.Platform" }`; without it the .NET 10 SDK refuses
`dotnet test` for MSTest.Sdk projects.

## WinUI app project settings

Every WinUI exe head uses these properties (verified in milestone M0 on Windows 11 ARM64):

```xml
<OutputType>WinExe</OutputType>
<TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
<TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
<Platforms>x64;ARM64</Platforms>
<UseWinUI>true</UseWinUI>
<WindowsPackageType>None</WindowsPackageType>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<SelfContained>true</SelfContained>
<EnableMsixTooling>true</EnableMsixTooling>
<SatelliteResourceLanguages>en</SatelliteResourceLanguages>
```

`EnableMsixTooling` is required even though BWC is not packaged. Without it, `dotnet publish`
leaves out the app's `.pri` file and compiled XAML, and the published app crashes at startup
in `Microsoft.UI.Xaml.dll` with exception code `0xC000027B`. The plain `bin` build output is
not affected, which hides the problem until publish.

## Commands

```
dotnet publish -c Release -r win-x64   -p:Platform=x64   -p:PublishReadyToRun=true -o publish/win-x64
dotnet publish -c Release -r win-arm64 -p:Platform=ARM64 -p:PublishReadyToRun=true -o publish/win-arm64
```

x64 builds run under emulation on ARM64 machines, so both architectures can be smoke-tested
on an ARM64 development machine. Performance numbers are only valid on the native
architecture.

A minimal self-contained WinUI app published this way is about 280 MB (x64) and 305 MB
(ARM64) before the installer compresses it.
