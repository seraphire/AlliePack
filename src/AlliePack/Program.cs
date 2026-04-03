using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommandLine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AlliePack
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(Run)
                .WithNotParsed(errors => { /* Error handling handled by CommandLineParser */ });
        }

        static void Run(Options options)
        {
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
                    return;
                }

                Console.WriteLine($"Reading config: {configPath}...");
                string yaml = File.ReadAllText(configPath);

                // Apply --define substitutions to raw YAML before parsing.
                // Replaces [KEY] with VALUE everywhere in the file, including
                // product fields like version, name, and path tokens.
                var defines = ParseDefines(options.Defines);
                if (defines.Count > 0)
                {
                    foreach (var kvp in defines)
                    {
                        string token = "[" + kvp.Key + "]";
                        yaml = yaml.Replace(token, kvp.Value);
                        if (options.Verbose)
                            Console.WriteLine($"  define: {token} -> {kvp.Value}");
                    }
                }

                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .WithTypeConverter(new ConditionalStringConverter())
                    .Build();

                var config = deserializer.Deserialize<AlliePackConfig>(yaml);

                // Resolve active release flags: --flag args take precedence over
                // defaultActiveFlags in the config; either may be empty.
                var activeFlags = options.Flags.Any()
                    ? options.Flags.ToList()
                    : config.DefaultActiveFlags;

                if (activeFlags.Any() && options.Verbose)
                    Console.WriteLine($"  active flags: {string.Join(", ", activeFlags)}");

                var resolver = new PathResolver(configPath, config.Aliases);
                var solutionResolver = new SolutionResolver(resolver);
                var builder = new InstallerBuilder(config, resolver, solutionResolver, options, activeFlags);

                Console.WriteLine($"Building MSI for {config.Product.Name} v{config.Product.Version}...");
                builder.Build();

                Console.WriteLine("Done.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal Error: {ex.Message}");
                if (options.Verbose)
                {
                    Console.WriteLine(ex.StackTrace);
                }
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
