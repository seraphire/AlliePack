# Quick Start

This guide takes you from nothing to a working MSI in about five minutes. You will need AlliePack and WiX installed — see [Installation](install.md) if you haven't done that yet.

## Step 1: Create the config file

In your project directory (or anywhere convenient), create a file named `allie-pack.yaml`:

```yaml
product:
  name: "My App"
  manufacturer: "My Company"
  version: "1.0.0.0"
  upgradeCode: "GENERATE-A-GUID-HERE"
  platform: "x64"

aliases:
  bin: "C:/path/to/your/build/output"

structure:
  - source: "bin:MyApp.exe"
  - source: "bin:*.dll"

shortcuts:
  - name: "My App"
    target: "[INSTALLDIR]\\MyApp.exe"
    folder: "[ProgramMenuFolder]"
```

**Replace these values:**

| Value | What to put here |
|---|---|
| `name` | Your product's display name |
| `manufacturer` | Your company name |
| `upgradeCode` | A stable GUID — generate one with `[System.Guid]::NewGuid()` in PowerShell |
| `bin` alias | The directory containing your built `.exe` and `.dll` files |
| `MyApp.exe` | The name of your application's executable |

## Step 2: Preview before building

Run AlliePack in report mode to confirm it finds your files before building an MSI:

```
AlliePack.exe allie-pack.yaml --report
```

You should see output like:

```
=== AlliePack Report ===
Product: My App 1.0.0.0 (My Company)

[INSTALLDIR]
  MyApp.exe
  SomeLibrary.dll
  AnotherLibrary.dll

Shortcuts:
  My App -> [INSTALLDIR]\MyApp.exe  (Start Menu\Programs)
```

If files are missing or paths look wrong, fix the `bin` alias path and run `--report` again.

## Step 3: Build the MSI

```
AlliePack.exe allie-pack.yaml --output dist\MyApp.msi
```

AlliePack resolves the files, generates a WiX project, compiles it, and writes the MSI to `dist\MyApp.msi`. The output looks like:

```
Building: My App 1.0.0.0
  Platform : x64
  Scope    : perMachine
  Output   : dist\MyApp.msi
Build complete: dist\MyApp.msi
```

## Step 4: Test the installer

Double-click `MyApp.msi` to run the installer, or test it silently:

```
msiexec /i dist\MyApp.msi /quiet /l*v install.log
```

Check `install.log` if something goes wrong.

## Step 5: Uninstall

```
msiexec /x dist\MyApp.msi /quiet
```

Or find it in **Settings > Apps** and uninstall from there.

---

## What's next?

| Goal | Where to look |
|---|---|
| Add registry keys, services, environment variables | [Schema Guide](schema-guide.md) |
| Use a .sln or .csproj as the source instead of a folder | [Cookbook: Adding a Visual Studio project](cookbook.md#adding-a-visual-studio-project) |
| Inject the version from the build | [Cookbook: Version from git tags](cookbook.md#version-from-git-tags) |
| Sign the MSI | [Signing](signing.md) |
| Build from a pipeline | [CI/CD Integration](ci-cd.md) |
| Understand all config options | [Schema Reference](schema-reference.md) |
