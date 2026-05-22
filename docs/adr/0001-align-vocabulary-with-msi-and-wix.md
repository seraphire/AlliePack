# Align AlliePack vocabulary with MSI and WiX terminology

AlliePack's domain language -- in YAML keys, docs, error messages, and the glossary -- follows MSI installer conventions first, then WiX v5 naming where MSI is ambiguous. Generic installer terms (e.g. "application bundle", "setup file") are avoided in favour of the precise MSI/WiX equivalents (Product, Package, Bundle, Component, Feature). This makes the tool approachable to developers who already know WiX, and means AlliePack's abstractions stay traceable to the underlying engine.

## Considered Options

- **Generic installer vocabulary** (e.g. "App", "Installer", "Setup") — more approachable to WiX newcomers but obscures the 1:1 mapping between AlliePack concepts and WiX elements, and breaks down as advanced features are added.
- **WiX vocabulary first** (chosen) — exact alignment makes the reach-through to raw WiX XML predictable; developers can read WiX docs and map directly to AlliePack behaviour.
