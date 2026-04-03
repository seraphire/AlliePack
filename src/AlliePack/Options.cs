using System.Collections.Generic;
using CommandLine;

namespace AlliePack
{
    public class Options
    {
        [Value(0, MetaName = "config", Required = false,
            HelpText = "Path to a config file, or a directory to search for allie-pack.yaml. " +
                       "Defaults to allie-pack.yaml in the current directory.")]
        public string ConfigPath { get; set; } = string.Empty;

        [Option('r', "report", Required = false, HelpText = "Generate a report of resolved files instead of building the MSI.")]
        public bool ReportOnly { get; set; }

        [Option('o', "output", Required = false, HelpText = "The output path for the generated MSI or report.")]
        public string? OutputPath { get; set; }

        [Option('v', "verbose", Required = false, HelpText = "Enable verbose output.")]
        public bool Verbose { get; set; }

        [Option('D', "define", Required = false, Separator = ',',
            HelpText = "Define a substitution token used in the config file. Format: KEY=VALUE. " +
                       "Replaces [KEY] anywhere in the YAML before parsing. " +
                       "Example: -D VERSION=2.1.0 -D SUFFIX=Beta")]
        public IEnumerable<string> Defines { get; set; } = new List<string>();
    }
}
