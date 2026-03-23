using System;
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

                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .Build();

                var config = deserializer.Deserialize<AlliePackConfig>(yaml);

                var resolver = new PathResolver(options.ConfigPath, config.Aliases);
                var builder = new InstallerBuilder(config, resolver, options);

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
    }
}
