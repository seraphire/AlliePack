# AlliePack

AlliePack is a YAML-driven installer wrapper designed to simplify the creation of MSI installers using the **WixSharp** and **WiX v4** toolchain. It allows developers to define their installer structure in a human-readable YAML format instead of complex XML or C# scripts.

## Key Features

- **YAML-Driven Configuration**: Define product information, aliases, and file structures in a clean `allie-pack.yaml` file.
- **Flexible Path Resolution**:
  - Support for built-in tokens: `[YamlDir]`, `[GitRoot]`, `[CurrentDir]`.
  - User-defined **Aliases** for quick reference to common build output or source directories.
  - **Glob Pattern** support for bulk file inclusion (e.g., `bin:*.dll`).
- **Dry-Run / Report Mode**: Generate a detailed report of the files and folders that will be included in the MSI without actually building it.
- **WiX v4 Integration**: Leverages the latest WiX Toolset via WixSharp for modern installer features.

## Getting Started

### Prerequisites

- .NET Framework 4.8.1 (for the tool itself)
- [WiX Toolset v4/v5](https://wixtoolset.org/) installed globally (`dotnet tool install --global wix`).
- Access to the files you wish to package.

### Basic Usage

1. Create an `allie-pack.yaml` file in your project root (see example below).
2. Run AlliePack from the command line:

```bash
# Generate a report of what will be in the MSI
AlliePack.exe allie-pack.yaml --report

# Build the actual MSI
AlliePack.exe allie-pack.yaml
```

### Configuration Example (`allie-pack.yaml`)

```yaml
product:
  name: "My Awesome App"
  manufacturer: "My Company"
  version: "1.0.0"
  description: "Description of the application"
  upgradeCode: "YOUR-GUID-HERE"
  installScope: "perMachine"

aliases:
  bin: "src/MyApp/bin/Release"
  docs: "documentation/pdf"

structure:
  - folder: "MainApp"
    contents:
      - source: "bin:MyApp.exe"
      - source: "bin:*.dll"
  - folder: "Documentation"
    contents:
      - source: "docs:user_manual.pdf"
```

## Project Structure (V0.1)

- `Program.cs`: Entry point and CLI parsing.
- `ConfigModels.cs`: YAML deserialization models.
- `PathResolver.cs`: Logic for resolving tokens, aliases, and globs.
- `InstallerBuilder.cs`: WiX/WixSharp project construction and MSI building.
- `Options.cs`: Command-line argument definitions.

## Future Roadmap

- [ ] Support for shortcut creation.
- [ ] Custom UI Dialogs and branding support.
- [ ] Advanced WiX features (Registry keys, Services, etc.) via YAML.
- [ ] Integrated WiX extension management.

## License

Copyright (c) 2026 Antigravity.
