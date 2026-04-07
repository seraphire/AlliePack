using System;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace AlliePack
{
    // -----------------------------------------------------------------------
    // ConditionalString
    //
    // A YAML field that may be either a plain scalar or a flag-conditional map:
    //
    //   # Scalar form (no release flags needed):
    //   installScope: "perUser"
    //
    //   # Conditional map form (resolved at build time via --flag):
    //   installScope:
    //     PerUser:    perUser
    //     PerMachine: perMachine
    //     _else:      perUser
    //
    // Call Resolve(activeFlags) to get the effective string value.
    // -----------------------------------------------------------------------

    public class ConditionalString
    {
        private readonly string? _scalar;
        private readonly Dictionary<string, string>? _map;

        public ConditionalString(string scalar) { _scalar = scalar; }
        public ConditionalString(Dictionary<string, string> map) { _map = map; }

        public string Resolve(IReadOnlyList<string> activeFlags)
        {
            if (_scalar != null) return _scalar;
            if (_map == null) return string.Empty;

            foreach (var flag in activeFlags)
                if (_map.TryGetValue(flag, out var val)) return val;

            if (_map.TryGetValue("_else", out var fallback)) return fallback;
            return string.Empty;
        }

        public override string ToString() => _scalar ?? $"({_map?.Count ?? 0}-entry conditional map)";
    }

    public class ConditionalStringConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(ConditionalString);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.Current is Scalar scalar)
            {
                parser.MoveNext();
                return new ConditionalString(scalar.Value);
            }
            if (parser.Current is MappingStart)
            {
                parser.MoveNext();
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (!(parser.Current is MappingEnd))
                {
                    var key = ((Scalar)parser.Current!).Value;
                    parser.MoveNext();
                    var value = ((Scalar)parser.Current!).Value;
                    parser.MoveNext();
                    map[key] = value;
                }
                parser.MoveNext(); // consume MappingEnd
                return new ConditionalString(map);
            }
            throw new InvalidOperationException("Expected scalar or mapping for ConditionalString");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
            => throw new NotImplementedException();
    }

    // -----------------------------------------------------------------------
    // Top-level config
    // -----------------------------------------------------------------------

    public class AlliePackConfig
    {
        [YamlMember(Alias = "product")]
        public ProductInfo Product { get; set; } = new();

        [YamlMember(Alias = "aliases")]
        public Dictionary<string, string> Aliases { get; set; } = new();

        [YamlMember(Alias = "structure")]
        public List<StructureElement> Structure { get; set; } = new();

        [YamlMember(Alias = "shortcuts")]
        public List<ShortcutInfo> Shortcuts { get; set; } = new();

        [YamlMember(Alias = "environment")]
        public List<EnvVarConfig> Environment { get; set; } = new();

        [YamlMember(Alias = "directories")]
        public List<DirectoryConfig> Directories { get; set; } = new();

        [YamlMember(Alias = "groups")]
        public List<FileGroupConfig> Groups { get; set; } = new();

        [YamlMember(Alias = "registry")]
        public List<RegistryConfig> Registry { get; set; } = new();

        // Raw WiX XML escape hatch
        [YamlMember(Alias = "wix")]
        public WixConfig? Wix { get; set; }

        // GAP-5: release flags declared in the config file
        [YamlMember(Alias = "releaseFlags")]
        public List<string> ReleaseFlags { get; set; } = new();

        // GAP-5: flags active when no --flag argument is passed
        [YamlMember(Alias = "defaultActiveFlags")]
        public List<string> DefaultActiveFlags { get; set; } = new();
    }

    // -----------------------------------------------------------------------
    // Product
    // -----------------------------------------------------------------------

    public class ProductInfo
    {
        public string Name { get; set; } = "My Product";
        public string Manufacturer { get; set; } = "My Company";
        public string Version { get; set; } = "1.0.0.0";
        public string Description { get; set; } = string.Empty;
        public string UpgradeCode { get; set; } = Guid.NewGuid().ToString();

        // GAP-5: supports conditional map (PerUser/PerMachine) or plain scalar
        [YamlMember(Alias = "installScope")]
        public ConditionalString InstallScope { get; set; } = new ConditionalString("perMachine");

        [YamlMember(Alias = "installDir")]
        public ConditionalString? InstallDir { get; set; }

        public string Platform { get; set; } = "x86"; // x86, x64, arm64

        [YamlMember(Alias = "licenseFile")]
        public string? LicenseFile { get; set; }
    }

    // -----------------------------------------------------------------------
    // Environment variables
    // -----------------------------------------------------------------------

    public class EnvVarConfig
    {
        [YamlMember(Alias = "name")]
        public string Name { get; set; } = string.Empty;

        // GAP-5: supports conditional map
        [YamlMember(Alias = "value")]
        public ConditionalString Value { get; set; } = new ConditionalString(string.Empty);

        // GAP-5: supports conditional map (user/machine).
        // Nullable so we can detect when it was not set in YAML, allowing
        // installScope: both to supply a smart default.
        [YamlMember(Alias = "scope")]
        public ConditionalString? Scope { get; set; }
    }

    // -----------------------------------------------------------------------
    // Named directories
    // -----------------------------------------------------------------------

    public class DirectoryConfig
    {
        [YamlMember(Alias = "id")]
        public string Id { get; set; } = string.Empty;

        // GAP-5: supports conditional map.
        // Ignored when Type is set.
        [YamlMember(Alias = "path")]
        public ConditionalString Path { get; set; } = new ConditionalString(string.Empty);

        // Well-known location shorthand: config, localdata, psmodules51, psmodules7,
        // desktop, startmenu, startup.
        // When set, Path is ignored and the base is resolved from the well-known type
        // table using the effective install scope.
        [YamlMember(Alias = "type")]
        public string? Type { get; set; }

        // Appended to the resolved type base.  Required when Type is set.
        [YamlMember(Alias = "subPath")]
        public string? SubPath { get; set; }
    }

    // -----------------------------------------------------------------------
    // File groups
    // -----------------------------------------------------------------------

    public class FileGroupItem
    {
        [YamlMember(Alias = "source")]
        public string Source { get; set; } = string.Empty;

        [YamlMember(Alias = "rename")]
        public string? Rename { get; set; }
    }

    public class FileGroupConfig
    {
        [YamlMember(Alias = "id")]
        public string Id { get; set; } = string.Empty;

        [YamlMember(Alias = "destinationDir")]
        public string DestinationDir { get; set; } = string.Empty;

        // GAP-4: condition: notExists -- skip if the destination file already exists
        [YamlMember(Alias = "condition")]
        public string? Condition { get; set; }

        [YamlMember(Alias = "files")]
        public List<FileGroupItem> Files { get; set; } = new();
    }

    // -----------------------------------------------------------------------
    // Shortcuts
    // -----------------------------------------------------------------------

    public class ShortcutInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? Icon { get; set; }
        public string Folder { get; set; } = string.Empty;
    }

    // -----------------------------------------------------------------------
    // Raw WiX XML escape hatch
    // -----------------------------------------------------------------------

    public class WixConfig
    {
        [YamlMember(Alias = "fragments")]
        public List<WixFragmentConfig> Fragments { get; set; } = new();
    }

    public class WixFragmentConfig
    {
        // Inline XML string -- Fragment element or any valid WiX top-level element
        [YamlMember(Alias = "inline")]
        public string? Inline { get; set; }

        // Path to a .wxs file; resolved via aliases and tokens
        [YamlMember(Alias = "file")]
        public string? File { get; set; }
    }

    // -----------------------------------------------------------------------
    // Registry values
    // -----------------------------------------------------------------------

    public class RegistryConfig
    {
        // Hive: HKLM, HKCU, HKCR, HKU
        // Aliases: LocalMachine, CurrentUser, ClassesRoot, Users
        [YamlMember(Alias = "root")]
        public string Root { get; set; } = "HKLM";

        // Registry key path under the hive, e.g. "Software\\MyCompany\\MyApp"
        [YamlMember(Alias = "key")]
        public string Key { get; set; } = string.Empty;

        // Value name.  Empty string or omitted = default value.
        [YamlMember(Alias = "name")]
        public string Name { get; set; } = string.Empty;

        // Value data.  Supports [INSTALLDIR] and other WiX properties.
        // Also accepts a conditional map for scope-variant values.
        [YamlMember(Alias = "value")]
        public ConditionalString Value { get; set; } = new ConditionalString(string.Empty);

        // Value type: string (default), expandString, multiString, dword, qword, binary
        [YamlMember(Alias = "type")]
        public string Type { get; set; } = "string";

        // Registry bitness override.
        // null (omitted) = use the installer platform (x64 installer -> 64-bit view, x86 -> 32-bit view).
        // true  = always write to the 64-bit registry view.
        // false = always write to the 32-bit/WOW64 view (useful for 32-bit COM registrations in x64 installers).
        [YamlMember(Alias = "win64")]
        public bool? Win64 { get; set; }
    }

    // -----------------------------------------------------------------------
    // Structure elements
    // -----------------------------------------------------------------------

    public class StructureElement
    {
        [YamlMember(Alias = "folder")]
        public string? FolderName { get; set; }

        [YamlMember(Alias = "destination")]
        public string? Destination { get; set; }

        [YamlMember(Alias = "source")]
        public string? Source { get; set; }

        [YamlMember(Alias = "solution")]
        public string? Solution { get; set; }

        [YamlMember(Alias = "project")]
        public string? Project { get; set; }

        [YamlMember(Alias = "configuration")]
        public string Configuration { get; set; } = "Release";

        [YamlMember(Alias = "platform")]
        public string Platform { get; set; } = "Any CPU";

        [YamlMember(Alias = "excludeProjects")]
        public List<string> ExcludeProjects { get; set; } = new();

        [YamlMember(Alias = "excludeFiles")]
        public List<string> ExcludeFiles { get; set; } = new();

        [YamlMember(Alias = "contents")]
        public List<StructureElement>? Contents { get; set; }
    }
}
