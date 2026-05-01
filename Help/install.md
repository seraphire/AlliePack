# Installation

## Prerequisites

### 1. .NET Framework 4.8.1

AlliePack runs on .NET Framework 4.8.1. This is included with Windows 10 (version 1903+) and Windows 11. On older machines or Windows Server, install it from Microsoft:

- [Download .NET Framework 4.8.1](https://dotnet.microsoft.com/download/dotnet-framework/net481)

To check whether it is already installed:

```powershell
Get-ChildItem "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" | Get-ItemProperty | Select-Object Release
```

A `Release` value of **533320** or higher means 4.8.1 is installed.

### 2. WiX Toolset v5

AlliePack uses WiX v5 as a dotnet global tool to compile the MSI.

```
dotnet tool install --global wix --version 5.*
```

Verify the install:

```
wix --version
```

You should see `5.x.x` or higher. If `dotnet` is not available, install the .NET SDK (any version 6+) from [dot.net](https://dot.net). The SDK is only needed to install the tool — AlliePack itself does not require it at build time.

> **Note:** On CI agents (Azure DevOps hosted agents, GitHub Actions `windows-latest`), the .NET SDK is pre-installed. Run the `dotnet tool install` step once at the start of your pipeline.

## Installing AlliePack

### Option A: Download the release

Download the latest `AlliePack.exe` from the releases page and place it somewhere on your `PATH`, or reference it by full path in your build scripts.

### Option B: Build from source

```
git clone <repository-url>
cd AlliePack
dotnet build src/AlliePack/AlliePack.csproj -c Release
```

The built executable is at `src/AlliePack/bin/Release/net481/AlliePack.exe`.

## Verifying the installation

Run AlliePack with no arguments to confirm it starts:

```
AlliePack.exe --help
```

You should see the usage summary. If you see an error about WiX not being found, verify that `wix.exe` is on your `PATH`:

```
where wix
```

If WiX was installed as a dotnet global tool and `where wix` fails, you may need to add the dotnet tools directory to your PATH:

```
# Typically: %USERPROFILE%\.dotnet\tools
[System.Environment]::SetEnvironmentVariable(
    "PATH",
    "$env:PATH;$env:USERPROFILE\.dotnet\tools",
    "User"
)
```

Then open a new terminal window and try again.

## CI/CD installation

See [CI/CD Integration](ci-cd.md) for pipeline-specific installation steps for Azure DevOps and GitHub Actions.
