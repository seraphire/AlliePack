# AlliePack

AlliePack is a YAML-driven MSI installer builder built on **WixSharp** and **WiX v4**. Define your installer in a human-readable `allie-pack.yaml` file instead of writing complex WiX XML or C# scripts.

## Features

- **YAML configuration** for product info, file structure, and shortcuts
- **Path resolution** with built-in tokens (`[YamlDir]`, `[GitRoot]`, `[CurrentDir]`) and user-defined aliases
- **Glob pattern** support for bulk file inclusion (e.g., `bin:*.dll`)
- **Visual Studio integration** -- resolve build outputs directly from `.sln` or `.csproj` files
- **Shortcut creation** for Start Menu, Desktop, or any WiX folder property
- **WPF managed UI** with standard installer dialogs (Welcome, InstallDir, Progress, Exit); optional license agreement dialog
- **Token substitution** via `--define KEY=VALUE` for injecting version numbers, paths, or any value into YAML at build time
- **Dry-run / report mode** to preview MSI contents without building
- **Platform support**: x86, x64, arm64
- **Install scope**: perMachine or perUser

## Prerequisites

- .NET Framework 4.8.1
- WiX Toolset v4 installed as a dotnet tool:
  ```
  dotnet tool install --global wix
  ```

## Usage

```
AlliePack.exe [config]  [options]

Arguments:
  config                    Path to a config file, or a directory containing
                            allie-pack.yaml. Omit to use allie-pack.yaml in
                            the current directory.

Options:
  -r, --report              Preview resolved files without building the MSI
  -o, --output <path>       Output path for the generated MSI
  -D, --define KEY=VALUE    Substitute [KEY] tokens in YAML before parsing
  -v, --verbose             Enable verbose output
```

### Examples

```
# Build the MSI
AlliePack.exe allie-pack.yaml

# Preview what will be in the MSI without building
AlliePack.exe allie-pack.yaml --report

# Inject version and build number at build time
AlliePack.exe allie-pack.yaml -D VERSION=2.1.0 -D BUILD=42

# Write MSI to a specific location
AlliePack.exe allie-pack.yaml --output C:\builds\MyApp-2.1.0.msi
```

## Config File Naming

| Scenario | Filename |
|---|---|
| Default file in a project directory | `allie-pack.yaml` |
| Named/project-specific config | `<project>.allie.yaml` (e.g., `myapp.allie.yaml`) |

When no config argument is given, AlliePack looks for `allie-pack.yaml` in the current directory. Passing a directory path does the same. Named configs must be referenced explicitly.

## Configuration Reference

### `product`

```yaml
product:
  name: "My App"
  manufacturer: "My Company"
  version: "1.0.0.0"         # Four-part version: Major.Minor.Build.Revision
  description: "My App installer"
  upgradeCode: "YOUR-GUID"   # Fixed GUID -- changing this breaks upgrades
  installScope: "perMachine" # perMachine or perUser
  platform: "x64"            # x86 (default), x64, arm64
  installDir: "[ProgramFiles]\\MyCompany\\MyApp"  # optional custom install path
  licenseFile: "license.rtf" # optional; adds license agreement dialog
```

### `aliases`

Aliases are short names for paths, used in `source:` fields with the `alias:path` syntax.

```yaml
aliases:
  bin: "src/MyApp/bin/Release/net481"
  assets: "resources/dist"
```

Built-in tokens that can appear anywhere in path values:

| Token | Resolves to |
|---|---|
| `[YamlDir]` | Directory containing the YAML config file |
| `[GitRoot]` | Root of the nearest git repository |
| `[CurrentDir]` | Current working directory |

### `structure`

Defines the folder and file hierarchy that will be installed. Elements can nest arbitrarily.

```yaml
structure:
  - folder: "App"
    contents:
      - source: "bin:MyApp.exe"
      - source: "bin:*.dll"
        excludeFiles:
          - "*.pdb"
          - "*.xml"

  - folder: "Plugins"
    contents:
      # Include all build outputs from a solution
      - solution: "src/MyApp.sln"
        configuration: "Release"
        platform: "Any CPU"
        excludeProjects:
          - "MyApp.Tests"
        excludeFiles:
          - "*.pdb"

      # Or from a single project
      - project: "src/MyApp.Core/MyApp.Core.csproj"
        configuration: "Release"
        excludeFiles:
          - "*.resources.dll"

      # Nested folders
      - folder: "Data"
        contents:
          - source: "assets:*.json"
```

**Source field syntax:**
- `alias:pattern` -- files matching `pattern` under the alias path
- `[Token]\path\pattern` -- token-based path with optional glob
- Relative paths are resolved from the YAML file's directory

### `shortcuts`

```yaml
shortcuts:
  - name: "My App"
    target: "[INSTALLDIR]\\App\\MyApp.exe"
    description: "Launch My App"
    folder: "[ProgramMenuFolder]"

  - name: "My App"
    target: "[INSTALLDIR]\\App\\MyApp.exe"
    folder: "[Desktop]"
```

**Folder values:**

| YAML value | Location |
|---|---|
| `[ProgramMenuFolder]` | Start Menu\Programs |
| `[Desktop]` or `[DesktopFolder]` | Desktop |

### `environment`

Set user or machine environment variables during installation. Variables are removed on uninstall.

```yaml
environment:
  - name: "MYAPP_HOME"
    value: "[INSTALLDIR]"
    scope: user       # user (default) or machine

  - name: "MYAPP_MODE"
    value: "production"
    scope: machine
```

## Project Structure

```
src/AlliePack/
  Program.cs            Entry point and CLI argument handling
  Options.cs            Command-line argument definitions
  ConfigModels.cs       YAML deserialization models
  PathResolver.cs       Token, alias, and glob path resolution
  SolutionResolver.cs   Visual Studio solution and project output detection
  ResolvedFile.cs       Source-to-destination file mapping model
  InstallerBuilder.cs   WixSharp project construction and MSI generation
```

## Roadmap

- [x] YAML-driven configuration
- [x] Path resolution (tokens, aliases, globs)
- [x] Visual Studio solution and project output resolution
- [x] Shortcut creation
- [x] WPF managed UI with standard dialogs
- [x] License agreement dialog
- [x] `--define` token substitution
- [x] x64 / arm64 platform support
- [ ] Registry keys and values
- [x] Environment variables
- [ ] Optional installer features (component selection)
- [ ] Release flags and conditional inclusion (multi-client configs)
- [ ] Modular YAML includes
- [ ] Variable substitution within YAML

## License

MIT Licensed.
