using CommandLine;

namespace AlliePack
{
    public class Options
    {
        [Value(0, MetaName = "config", Required = true, HelpText = "Path to the allie-pack.yaml configuration file.")]
        public string ConfigPath { get; set; } = string.Empty;

        [Option('r', "report", Required = false, HelpText = "Generate a report of resolved files instead of building the MSI.")]
        public bool ReportOnly { get; set; }

        [Option('o', "output", Required = false, HelpText = "The output path for the generated MSI or report.")]
        public string? OutputPath { get; set; }

        [Option('v', "verbose", Required = false, HelpText = "Enable verbose output.")]
        public bool Verbose { get; set; }
    }
}
