using System;
using System.Collections.Generic;
using System.IO;
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
                if (!File.Exists(options.ConfigPath))
                {
                    Console.WriteLine($"Error: Configuration file not found at {options.ConfigPath}");
                    return;
                }

                Console.WriteLine($"Reading config: {options.ConfigPath}...");
                string yaml = File.ReadAllText(options.ConfigPath);

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
                    .Build();

                var config = deserializer.Deserialize<AlliePackConfig>(yaml);

                var resolver = new PathResolver(options.ConfigPath, config.Aliases);
                var solutionResolver = new SolutionResolver(resolver);
                var builder = new InstallerBuilder(config, resolver, solutionResolver, options);

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
