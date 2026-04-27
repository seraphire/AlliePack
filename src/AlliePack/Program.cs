using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CommandLine;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AlliePack
{
    internal class Program
    {
        static void Main(string[] args)
        {
            CommandLine.Parser.Default.ParseArguments<Options>(args)
                .WithParsed(Run)
                .WithNotParsed(errors => { /* Error handling handled by CommandLineParser */ });
        }

        static string GetVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                       ?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "unknown";
        }

        static void Run(Options options)
        {
            Console.WriteLine($"AlliePack v{GetVersion()}");
            Console.WriteLine();

            string yaml = string.Empty;   // hoisted so the YamlException catch can show the offending line
            try
            {
                // Resolve config path: directory -> allie-pack.yaml, empty -> cwd/allie-pack.yaml
                string configPath = options.ConfigPath;
                if (string.IsNullOrEmpty(configPath))
                    configPath = Path.Combine(Directory.GetCurrentDirectory(), "allie-pack.yaml");
                else if (Directory.Exists(configPath))
                    configPath = Path.Combine(configPath, "allie-pack.yaml");

                if (!File.Exists(configPath))
                {
                    Console.WriteLine($"Error: Configuration file not found at {configPath}");
                    Console.WriteLine("Create an allie-pack.yaml in the current directory, or pass a path as the first argument.");
                    Console.WriteLine("Run with --help to see all options.");
                    return;
                }

                Console.WriteLine($"Reading config: {configPath}...");
                yaml = File.ReadAllText(configPath);

                // Parse YAML first -- no pre-processing.
                // --define values are folded into PathResolver as named tokens so they
                // are substituted at resolution time, not in raw YAML text.  This means
                // Windows backslash paths from CI variables (e.g. $(Build.SourcesDirectory))
                // are never seen by the YAML parser and cannot cause escape-sequence errors.
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .WithTypeConverter(new ConditionalStringConverter())
                    .WithTypeConverter(new VersionSourceConverter())
                    .Build();

                var config = deserializer.Deserialize<AlliePackConfig>(yaml);

                var defines = ParseDefines(options.Defines);
                if (defines.Count > 0 && options.IsVerbose)
                {
                    foreach (var kvp in defines)
                        Console.WriteLine($"  define: [{kvp.Key}] -> {kvp.Value}");
                }

                // Resolve active release flags: --flag args take precedence over
                // defaultActiveFlags in the config; either may be empty.
                var activeFlags = options.Flags.Any()
                    ? options.Flags.ToList()
                    : config.DefaultActiveFlags;

                if (activeFlags.Any() && options.IsVerbose)
                    Console.WriteLine($"  active flags: {string.Join(", ", activeFlags)}");

                // defines overlay the paths: block; command-line always wins.
                var resolver = new PathResolver(configPath, config.Aliases, config.Paths, defines);
                var solutionResolver = new SolutionResolver(resolver, options.Debug);
                var builder = new InstallerBuilder(config, resolver, solutionResolver, options, activeFlags);

                Console.WriteLine($"Building MSI for {config.Product.Name} v{config.Product.Version.Resolve(resolver)}...");
                builder.Build();

                Console.WriteLine("Done.");
            }
            catch (YamlException ex)
            {
                // YamlDotNet exceptions carry precise source location -- always show it.
                int errLine = Convert.ToInt32(ex.Start.Line);
                int errCol  = Convert.ToInt32(ex.Start.Column);
                Console.WriteLine($"YAML Error at line {errLine}, column {errCol}: {ex.Message}");

                // Print the offending line with a caret so the problem is immediately visible.
                try
                {
                    string[] lines = yaml.Split('\n');
                    if (errLine >= 1 && errLine <= lines.Length)
                    {
                        string offending = lines[errLine - 1].TrimEnd('\r');
                        Console.WriteLine($"  {offending}");
                        Console.WriteLine($"  {new string(' ', Math.Max(0, errCol - 1))}^");
                    }
                }
                catch { /* best-effort */ }

                if (options.IsVerbose)
                    Console.WriteLine(ex.StackTrace);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal Error: {ex.Message}");
                if (options.IsVerbose)
                    Console.WriteLine(ex.StackTrace);
            }
        }

        /// <summary>
        /// Parses KEY=VALUE define strings into a dictionary.
        /// Keys are case-insensitive. Values may contain '=' characters.
        /// </summary>
        static Dictionary<string, string> ParseDefines(IEnumerable<string> defines)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var define in defines)
            {
                int idx = define.IndexOf('=');
                if (idx > 0)
                {
                    string key = define.Substring(0, idx).Trim();
                    string value = define.Substring(idx + 1);
                    result[key] = value;
                }
                else
                {
                    Console.WriteLine($"Warning: Ignoring malformed --define '{define}' (expected KEY=VALUE)");
                }
            }
            return result;
        }
    }
}
