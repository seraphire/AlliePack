using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
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
    // VersionSource
    //
    // A YAML field that may be either a plain scalar or a file-extraction block:
    //
    //   # Scalar form:
    //   version: "1.0.0.0"
    //
    //   # File form (reads FileVersionInfo from the built binary):
    //   version:
    //     file: "bin:MyApp.exe"
    //     source: "file-version"     # or "product-version" (default: file-version)
    //
    // Call Resolve(resolver) to get the effective string value.
    // -----------------------------------------------------------------------

    public class VersionSource
    {
        private readonly string? _literal;
        private readonly string? _file;
        private readonly string _source;
        private readonly string? _tagPrefix;

        public VersionSource(string literal) { _literal = literal; _source = "file-version"; }

        private VersionSource(string? file, string source, string? tagPrefix)
        {
            _file = file;
            _source = source;
            _tagPrefix = tagPrefix;
        }

        public static VersionSource ForFile(string file, string source) => new(file, source, null);
        public static VersionSource ForGit(string? tagPrefix) => new(null, "git-tag", tagPrefix);

        public string Resolve(PathResolver resolver)
        {
            if (_literal != null) return _literal;
            if (_source.Equals("git-tag", StringComparison.OrdinalIgnoreCase))
                return ResolveFromGit(resolver.WorkingDirectory);
            string path = resolver.Resolve(_file!);
            var fvi = FileVersionInfo.GetVersionInfo(path);
            if (_source.Equals("product-version", StringComparison.OrdinalIgnoreCase))
                return fvi.ProductVersion ?? "1.0.0.0";
            return fvi.FileVersion ?? "1.0.0.0";
        }

        private string ResolveFromGit(string workDir)
        {
            string prefix = _tagPrefix ?? "v";
            string? described = RunGit($"describe --tags --long --match \"{prefix}*\"", workDir);
            if (described != null)
            {
                // Format: v1.2.3-7-gabcdef  =>  strip prefix, remove hash, split count
                if (described.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    described = described.Substring(prefix.Length);

                // Remove trailing -g<hash>
                described = Regex.Replace(described, @"-g[0-9a-f]+$", "");

                int dash = described.LastIndexOf('-');
                if (dash > 0 && int.TryParse(described.Substring(dash + 1), out int commitCount))
                {
                    string tag = described.Substring(0, dash);
                    var parts = tag.Split('.');
                    string major = parts.Length > 0 ? parts[0] : "0";
                    string minor = parts.Length > 1 ? parts[1] : "0";
                    string patch = parts.Length > 2 ? parts[2] : "0";
                    return $"{major}.{minor}.{patch}.{commitCount}";
                }
            }

            // No matching tag — fall back to 0.0.0.{total commits}
            string? countStr = RunGit("rev-list --count HEAD", workDir);
            int totalCommits = 0;
            if (countStr != null) int.TryParse(countStr.Trim(), out totalCommits);
            return $"0.0.0.{totalCommits}";
        }

        private static string? RunGit(string args, string workDir)
        {
            try
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    WorkingDirectory = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process == null) return null;
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return process.ExitCode == 0 ? output : null;
            }
            catch
            {
                return null;
            }
        }

        public override string ToString() => _literal ?? (_source == "git-tag" ? "(from git)" : $"(from {_file})");
    }

    public class VersionSourceConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(VersionSource);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.Current is Scalar scalar)
            {
                parser.MoveNext();
                return new VersionSource(scalar.Value);
            }
            if (parser.Current is MappingStart)
            {
                parser.MoveNext();
                string? file = null;
                string source = "file-version";
                string? tagPrefix = null;
                while (!(parser.Current is MappingEnd))
                {
                    string key = ((Scalar)parser.Current!).Value;
                    parser.MoveNext();
                    string value = ((Scalar)parser.Current!).Value;
                    parser.MoveNext();
                    if (key.Equals("file", StringComparison.OrdinalIgnoreCase)) file = value;
                    else if (key.Equals("source", StringComparison.OrdinalIgnoreCase)) source = value;
                    else if (key.Equals("tagPrefix", StringComparison.OrdinalIgnoreCase)) tagPrefix = value;
                }
                parser.MoveNext(); // consume MappingEnd
                if (source.Equals("git-tag", StringComparison.OrdinalIgnoreCase))
                    return VersionSource.ForGit(tagPrefix);
                if (file == null) throw new InvalidOperationException("version block requires a 'file' key");
                return VersionSource.ForFile(file, source);
            }
            throw new InvalidOperationException("Expected scalar or mapping for VersionSource");
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

        [YamlMember(Alias = "services")]
        public List<ServiceConfig> Services { get; set; } = new();

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
        public VersionSource Version { get; set; } = new VersionSource("1.0.0.0");
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
    // Windows Services
    // -----------------------------------------------------------------------

    public class ServiceConfig
    {
        // Internal service name used by the SCM (sc.exe, Get-Service).  Must be unique.
        [YamlMember(Alias = "name")]
        public string Name { get; set; } = string.Empty;

        // Human-readable name shown in the Services panel.  Defaults to name.
        [YamlMember(Alias = "displayName")]
        public string? DisplayName { get; set; }

        [YamlMember(Alias = "description")]
        public string? Description { get; set; }

        // Path to the service executable as it will appear after install.
        // Supports [INSTALLDIR] and other WiX properties.
        // Example: "[INSTALLDIR]\\myservice.exe"
        [YamlMember(Alias = "executable")]
        public string Executable { get; set; } = string.Empty;

        // Optional command-line arguments passed to the service executable.
        [YamlMember(Alias = "arguments")]
        public string? Arguments { get; set; }

        // Service logon account.
        // Well-known values: LocalSystem (default), LocalService, NetworkService.
        // Domain accounts: "DOMAIN\\username" (requires password:).
        [YamlMember(Alias = "account")]
        public string Account { get; set; } = "LocalSystem";

        // Password for domain accounts.  Omit for well-known accounts.
        [YamlMember(Alias = "password")]
        public string? Password { get; set; }

        // Startup type: auto | demand | disabled | boot | system.  Default: auto.
        [YamlMember(Alias = "start")]
        public string Start { get; set; } = "auto";

        // Service type: ownProcess (default) | shareProcess.
        [YamlMember(Alias = "type")]
        public string Type { get; set; } = "ownProcess";

        // Error control: ignore | normal (default) | critical.
        [YamlMember(Alias = "errorControl")]
        public string ErrorControl { get; set; } = "normal";

        // Allow service to interact with the desktop (legacy; rarely needed).
        [YamlMember(Alias = "interactive")]
        public bool? Interactive { get; set; }

        // Delay startup after boot (Windows Vista+, auto-start services only).
        [YamlMember(Alias = "delayedAutoStart")]
        public bool? DelayedAutoStart { get; set; }

        // Failure / recovery actions.
        [YamlMember(Alias = "onFailure")]
        public ServiceFailureConfig? OnFailure { get; set; }

        // Service names or group names this service depends on.
        [YamlMember(Alias = "dependsOn")]
        public List<string> DependsOn { get; set; } = new();
    }

    public class ServiceFailureConfig
    {
        // Action on 1st / 2nd / 3rd failure: none (default) | restart | reboot | runCommand.
        [YamlMember(Alias = "first")]
        public string First { get; set; } = "none";

        [YamlMember(Alias = "second")]
        public string Second { get; set; } = "none";

        [YamlMember(Alias = "third")]
        public string Third { get; set; } = "none";

        // Reset the failure count after this many days of successful operation.
        [YamlMember(Alias = "resetAfterDays")]
        public int? ResetAfterDays { get; set; }

        // Delay in seconds before restarting the service after a failure.
        [YamlMember(Alias = "restartDelaySeconds")]
        public int? RestartDelaySeconds { get; set; }
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
