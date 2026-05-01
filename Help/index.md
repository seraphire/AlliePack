# AlliePack Documentation

AlliePack builds Windows MSI installers from a single YAML file. No WiX XML. No C# scripts. Just a clean config that describes what your installer should do.

---

## Getting started

| | |
|---|---|
| [Install](install.md) | Prerequisites and installation steps |
| [Quick Start](quick-start.md) | Go from zero to a working MSI in five minutes |
| [Overview](overview.md) | What AlliePack is, why it exists, and when to use it |

## Reference

| | |
|---|---|
| [Schema Reference](schema-reference.md) | Complete field-by-field reference for `allie-pack.yaml` |
| [Schema Guide](schema-guide.md) | How AlliePack config works — concepts, path resolution, tokens, structure hierarchy |
| [CLI Reference](cli-reference.md) | All command-line options and environment variables |
| [Signing](signing.md) | Sign MSIs and packaged files — thumbprint, PFX, Azure Trusted Signing |

## Guides

| | |
|---|---|
| [Cookbook](cookbook.md) | How-to recipes: adding projects, path overrides, signing, services, flags, and more |
| [CI/CD Integration](ci-cd.md) | Azure DevOps and GitHub Actions pipeline examples |
| [Troubleshooting](troubleshooting.md) | Common errors and how to fix them |

---

## At a glance

```yaml
product:
  name: "My App"
  manufacturer: "My Company"
  version: "1.0.0.0"
  upgradeCode: "YOUR-STABLE-GUID"

aliases:
  bin: "[GitRoot]/src/MyApp/bin/Release/net481"

structure:
  - source: "bin:MyApp.exe"
  - source: "bin:*.dll"

shortcuts:
  - name: "My App"
    target: "[INSTALLDIR]\\MyApp.exe"
    folder: "[ProgramMenuFolder]"
```

```
AlliePack.exe allie-pack.yaml --output dist\MyApp.msi
```

That's it. AlliePack resolves the paths, assembles the WiX project, and hands the compiled MSI back to you.
