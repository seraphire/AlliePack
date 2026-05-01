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
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: git version resolution failed ({ex.GetType().Name}: {ex.Message}); version will fall back to commit count.");
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

        // Named build-machine paths, usable as [name] tokens anywhere in the config.
        // Values may contain built-in tokens ([GitRoot], [YamlDir], [CurrentDir]).
        // Any entry can be overridden on the command line with --define name=value.
        [YamlMember(Alias = "paths")]
        public Dictionary<string, string> Paths { get; set; } = new();

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

        // Optional: explicit path to the directory containing wix.exe.
        // Use when multiple WiX versions are installed and you need to pin one.
        // Also honoured via the WIXSHARP_WIXLOCATION environment variable.
        [YamlMember(Alias = "wixToolsPath")]
        public string? WixToolsPath { get; set; }

        // GAP-5: release flags declared in the config file
        [YamlMember(Alias = "releaseFlags")]
        public List<string> ReleaseFlags { get; set; } = new();

        // GAP-5: flags active when no --flag argument is passed
        [YamlMember(Alias = "defaultActiveFlags")]
        public List<string> DefaultActiveFlags { get; set; } = new();

        [YamlMember(Alias = "signing")]
        public SigningConfig? Signing { get; set; }

        [YamlMember(Alias = "features")]
        public List<FeatureConfig> Features { get; set; } = new();
    }

    // -----------------------------------------------------------------------
    // Code signing
    //
    // Optional top-level block.  Exactly one signing provider must be chosen:
    //
    //   thumbprint  -- cert already installed in the Windows cert store
    //   pfx         -- PFX file on disk
    //   azure       -- Azure Trusted Signing (calls signtool + Azure dlib)
    //   command     -- arbitrary shell command; use {file} as the path placeholder
    //
    // timestampUrl, signToolPath, and files: apply to all signtool-based
    // providers (thumbprint, pfx, azure).  They are ignored for command:.
    // -----------------------------------------------------------------------

    public class SigningConfig
    {
        // SHA1 thumbprint of a certificate already installed in the Windows cert store.
        [YamlMember(Alias = "thumbprint")]
        public string? Thumbprint { get; set; }

        // Path to a PFX file.  Resolved via aliases and path tokens.
        [YamlMember(Alias = "pfx")]
        public string? Pfx { get; set; }

        // Password for the PFX file.  Supports [TOKEN] substitution so secrets
        // can be injected at build time via --define SIGN_PASSWORD=<value>.
        [YamlMember(Alias = "pfxPassword")]
        public string? PfxPassword { get; set; }

        // Azure Trusted Signing provider.  Mutually exclusive with thumbprint/pfx/command.
        [YamlMember(Alias = "azure")]
        public AzureSigningConfig? Azure { get; set; }

        // Arbitrary signing command.  AlliePack substitutes [TOKEN] and {file},
        // then runs the result via cmd.exe.  Mutually exclusive with all other providers.
        // Example: 'AzureSignTool.exe sign -kvu "[KV_URL]" -kvc "[CERT]" -fd sha256 "{file}"'
        [YamlMember(Alias = "command")]
        public string? Command { get; set; }

        // RFC 3161 timestamp server URL.  Recommended for production signing.
        // Required for azure: (certificates are valid for 3 days only).
        [YamlMember(Alias = "timestampUrl")]
        public string? TimestampUrl { get; set; }

        // Explicit path to signtool.exe.  Discovered via PATH and Windows SDK
        // locations when omitted.  Not used by command:.
        [YamlMember(Alias = "signToolPath")]
        public string? SignToolPath { get; set; }

        // Optional: sign individual files before packaging them into the MSI.
        // When absent, only the MSI itself is signed.
        [YamlMember(Alias = "files")]
        public FileSigningConfig? Files { get; set; }
    }

    // -----------------------------------------------------------------------
    // Azure Trusted Signing provider (signing.azure:)
    //
    // AlliePack generates a metadata.json temp file from these fields and
    // passes it to signtool via /dmdf.  No manual metadata.json needed.
    //
    //   signing:
    //     azure:
    //       endpoint: "https://eus.codesigning.azure.net"
    //       account: "MySigningAccount"
    //       certificateProfile: "MyProfile"
    //       dlibPath: "C:\Tools\x64\Azure.CodeSigning.Dlib.dll"
    //       correlationId: "[BUILD_ID]"     # optional; supports tokens
    //     timestampUrl: "http://timestamp.acs.microsoft.com"
    // -----------------------------------------------------------------------

    public class AzureSigningConfig
    {
        // Regional endpoint, e.g. https://eus.codesigning.azure.net
        [YamlMember(Alias = "endpoint")]
        public string Endpoint { get; set; } = "";

        // Azure Trusted Signing account name.
        [YamlMember(Alias = "account")]
        public string Account { get; set; } = "";

        // Certificate profile name within the account.
        [YamlMember(Alias = "certificateProfile")]
        public string CertificateProfile { get; set; } = "";

        // Path to Azure.CodeSigning.Dlib.dll.
        // Install via: winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
        [YamlMember(Alias = "dlibPath")]
        public string? DlibPath { get; set; }

        // Optional correlation ID passed to Azure for audit tracing.
        // Supports [TOKEN] substitution (e.g. [BUILD_ID] from a pipeline variable).
        [YamlMember(Alias = "correlationId")]
        public string? CorrelationId { get; set; }
    }

    // -----------------------------------------------------------------------
    // Per-file signing config (signing.files:)
    //
    //   signing:
    //     thumbprint: "ABCDEF..."
    //     files:
    //       mode: unsigned              # all | unsigned (default)
    //       include: ["*.exe", "*.dll"] # filename globs; SIP check used when omitted
    //       exclude: ["*.resources.dll"]
    //
    // mode: unsigned  -- skip files that already carry an Authenticode signature
    // mode: all       -- sign every candidate regardless of existing signature
    //
    // When include is omitted, AlliePack calls CryptSIPRetrieveSubjectGuid to
    // ask Windows whether each file is signable -- the same check signtool
    // performs internally.  Files with no registered SIP handler are skipped.
    // -----------------------------------------------------------------------

    public class FileSigningConfig
    {
        // "all" | "unsigned" (default)
        [YamlMember(Alias = "mode")]
        public string Mode { get; set; } = "unsigned";

        // Filename glob patterns.  When null (omitted) the SIP check is the gate.
        [YamlMember(Alias = "include")]
        public List<string>? Include { get; set; }

        [YamlMember(Alias = "exclude")]
        public List<string> Exclude { get; set; } = new();
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
        public string? UpgradeCode { get; set; }

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

        // permanent: true -- file survives uninstall (Component:Permanent=yes)
        [YamlMember(Alias = "permanent")]
        public bool Permanent { get; set; }

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

        // Startup type (matches WiX/SCM names): auto | demand | disabled | boot | system.
        // "manual" is accepted as an alias for "demand".  Default: auto.
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

        // MSBuild platform name used to locate the project's output directory.
        // Common values: x86, x64, AnyCPU.
        // Note: MSBuild uses "AnyCPU" (no space) in .csproj conditions even though
        // Visual Studio displays it as "Any CPU".  Both forms are accepted here;
        // SolutionResolver normalises the value before matching.
        [YamlMember(Alias = "platform")]
        public string Platform { get; set; } = "AnyCPU";

        // When non-empty, only projects whose names appear in this list are included.
        // Acts as a whitelist; excludeProjects is applied afterward as an additional filter.
        // Useful when referencing a large solution but only needing one project's output.
        [YamlMember(Alias = "includeProjects")]
        public List<string> IncludeProjects { get; set; } = new();

        [YamlMember(Alias = "excludeProjects")]
        public List<string> ExcludeProjects { get; set; } = new();

        [YamlMember(Alias = "excludeFiles")]
        public List<string> ExcludeFiles { get; set; } = new();

        // What to do when a source/project/solution element resolves to zero files.
        //   warn   -- print a warning and continue (default)
        //   error  -- abort the build with an error
        //   ignore -- silently skip
        [YamlMember(Alias = "onEmpty")]
        public string OnEmpty { get; set; } = "warn";

        [YamlMember(Alias = "contents")]
        public List<StructureElement>? Contents { get; set; }
    }

    // -----------------------------------------------------------------------
    // Features
    //
    // Optional list of independently selectable installer features.  Each
    // feature maps to a WiX <Feature> element and gets its own checkbox in
    // the FeaturesDialog presented to the user during install.
    //
    // When features: is present, the top-level structure:/shortcuts:/etc.
    // blocks become "always-installed" base content.  For a bundle of N
    // completely independent programs with no shared base, leave the top-level
    // content blocks empty and put everything in features:.
    //
    // Example -- 4 independent service programs in one installer:
    //
    //   features:
    //     - id: ServiceA
    //       name: "Service A"
    //       description: "Handles order processing"
    //       default: true
    //       structure:
    //         - project: services/ServiceA/ServiceA.csproj
    //       services:
    //         - name: ServiceA
    //           executable: "[INSTALLDIR]\\ServiceA.exe"
    //
    //     - id: ServiceB
    //       name: "Service B"
    //       default: false
    //       structure:
    //         - project: services/ServiceB/ServiceB.csproj
    //       services:
    //         - name: ServiceB
    //           executable: "[INSTALLDIR]\\ServiceB.exe"
    //
    // Supported per-feature blocks:
    //   structure, shortcuts, environment, registry, services, groups
    //
    // display values: collapse (default) | expand | hidden
    // default: true (checked) | false (unchecked) -- whether the feature is
    //          selected when the dialog first opens
    // -----------------------------------------------------------------------

    public class FeatureConfig
    {
        // Short identifier used internally; not shown to the user.
        [YamlMember(Alias = "id")]
        public string Id { get; set; } = string.Empty;

        // Display name shown in the FeaturesDialog tree.
        [YamlMember(Alias = "name")]
        public string Name { get; set; } = string.Empty;

        // Optional description shown below the tree when this feature is selected.
        [YamlMember(Alias = "description")]
        public string Description { get; set; } = string.Empty;

        // Whether the feature is checked by default.  Default: true.
        [YamlMember(Alias = "default")]
        public bool Default { get; set; } = true;

        // Initial tree node state: collapse (default) | expand | hidden.
        [YamlMember(Alias = "display")]
        public string Display { get; set; } = "collapse";

        [YamlMember(Alias = "structure")]
        public List<StructureElement> Structure { get; set; } = new();

        [YamlMember(Alias = "shortcuts")]
        public List<ShortcutInfo> Shortcuts { get; set; } = new();

        [YamlMember(Alias = "environment")]
        public List<EnvVarConfig> Environment { get; set; } = new();

        [YamlMember(Alias = "registry")]
        public List<RegistryConfig> Registry { get; set; } = new();

        [YamlMember(Alias = "services")]
        public List<ServiceConfig> Services { get; set; } = new();

        [YamlMember(Alias = "groups")]
        public List<FileGroupConfig> Groups { get; set; } = new();
    }
}
