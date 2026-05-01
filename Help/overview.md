# Overview

## What is AlliePack?

AlliePack is a command-line tool that builds Windows MSI installer packages from a YAML configuration file. You describe what your installer should contain — files, shortcuts, registry keys, services, environment variables — and AlliePack handles the WiX/MSI mechanics automatically.

The output is a standard Windows Installer `.msi` file that installs cleanly on any Windows machine, supports silent installation, upgrade detection, and uninstall through Add/Remove Programs.

## Why does it exist?

Building an MSI the traditional way means learning WiX XML or writing C# scripts for WixSharp. Both are powerful, but both have steep learning curves and produce a lot of boilerplate. A typical WiX project for a modest application can run to hundreds of lines of XML before you've added anything specific to your app.

AlliePack takes the opposite approach: you write only what's specific to your product. Everything else has a sensible default. A working installer config is a dozen lines of YAML.

## Design philosophy

**Progressive complexity.** A simple installer stays simple. Advanced features — release flags, named directories, Windows services, signing — only appear in configs that use them. You don't have to understand them to get started, and they don't impose themselves on configs that don't need them.

**Manage complexity — don't hide it.** Every abstraction has a reach-through. If you know WiX, you can inject raw WiX XML fragments directly into the generated document (`wix.fragments:`). If you know what AlliePack produces, you can use `--keep-wxs` to inspect the generated `.wxs` source and trace every element back to the YAML that produced it.

**New top-level blocks over crowding `product:`.** Each feature gets its own section. If you don't use `services:`, that section doesn't exist in your config. Your file describes exactly what your installer does — nothing more.

## When to use AlliePack

AlliePack is a good fit if you want to:

- Build an MSI from a Visual Studio project or solution without learning WiX
- Maintain your installer config in source control alongside your application code, as readable YAML
- Produce consistent, repeatable MSI builds from a CI/CD pipeline
- Sign your installer and its packaged files as part of the build
- Support per-user and per-machine installation from a single config
- Package services, registry keys, environment variables, and shortcuts without writing XML

It is **not** a good fit if you need:

- A bootstrapper EXE that chains prerequisites (.NET, VC++ redist) — that requires Burn/WiX bundles, which are on the roadmap but not yet supported
- Complex custom actions written in managed code
- COM/DCOM registration, IIS configuration, or ODBC data sources — these need raw WiX fragments for now

## How it works

```
allie-pack.yaml
      |
      v
  AlliePack.exe  (reads YAML, resolves paths, builds WixSharp object model)
      |
      v
  WixSharp / WiX v5  (compiles to MSI)
      |
      v
  MyApp.msi
```

AlliePack builds a WixSharp project in memory, lets WiX compile it to an MSI, and optionally signs the result. The intermediate `.wxs` source can be preserved with `--keep-wxs` for inspection.
