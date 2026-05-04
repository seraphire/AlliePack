using System;
using System.IO;
using CommandLine;

namespace MsiInspector
{
    /// <summary>
    /// Thin CLI over the MsiInspector library. Library is the product;
    /// CLI is a 50-line consumer that demonstrates the API works and is
    /// usable from build pipelines, scripts, etc.
    /// </summary>
    internal static class Program
    {
        [Verb("dump", HelpText = "Dump an MSI to a directory of IDT files (one per table).")]
        private sealed class DumpOptions
        {
            [Value(0, MetaName = "msi", Required = true, HelpText = "Path to the MSI file.")]
            public string MsiPath { get; set; } = string.Empty;

            [Option('o', "output", Required = true, HelpText = "Output directory for IDT files.")]
            public string OutputDir { get; set; } = string.Empty;

            [Option("no-summary", HelpText = "Skip writing the _SummaryInformation.txt sidecar.")]
            public bool NoSummary { get; set; }
        }

        [Verb("query", HelpText = "Run a raw MSI SQL query and print results.")]
        private sealed class QueryOptions
        {
            [Value(0, MetaName = "msi", Required = true, HelpText = "Path to the MSI file.")]
            public string MsiPath { get; set; } = string.Empty;

            [Value(1, MetaName = "sql", Required = true, HelpText = "MSI SQL query to execute.")]
            public string Sql { get; set; } = string.Empty;
        }

        [Verb("tables", HelpText = "List the user tables present in an MSI.")]
        private sealed class TablesOptions
        {
            [Value(0, MetaName = "msi", Required = true, HelpText = "Path to the MSI file.")]
            public string MsiPath { get; set; } = string.Empty;
        }

        public static int Main(string[] args)
        {
            try
            {
                return Parser.Default
                    .ParseArguments<DumpOptions, QueryOptions, TablesOptions>(args)
                    .MapResult(
                        (DumpOptions o)   => RunDump(o),
                        (QueryOptions o)  => RunQuery(o),
                        (TablesOptions o) => RunTables(o),
                        _ => 1);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("MsiInspector error: " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return 2;
            }
        }

        private static int RunDump(DumpOptions o)
        {
            if (!File.Exists(o.MsiPath))
            {
                Console.Error.WriteLine("MSI not found: " + o.MsiPath);
                return 1;
            }

            var options = new MsiInspector.DumpOptions { IncludeSummary = !o.NoSummary };
            IdtDumper.Dump(o.MsiPath, o.OutputDir, options);
            Console.Out.WriteLine("Dumped " + Path.GetFileName(o.MsiPath) + " -> " + Path.GetFullPath(o.OutputDir));
            return 0;
        }

        private static int RunQuery(QueryOptions o)
        {
            if (!File.Exists(o.MsiPath))
            {
                Console.Error.WriteLine("MSI not found: " + o.MsiPath);
                return 1;
            }

            using var inspector = new Inspector(o.MsiPath);
            var first = true;
            foreach (var row in inspector.Query(o.Sql))
            {
                if (first)
                {
                    Console.Out.WriteLine(string.Join("\t", row.Keys));
                    first = false;
                }
                Console.Out.WriteLine(string.Join("\t", FormatRowValues(row)));
            }
            if (first)
            {
                Console.Out.WriteLine("(no rows)");
            }
            return 0;
        }

        private static int RunTables(TablesOptions o)
        {
            if (!File.Exists(o.MsiPath))
            {
                Console.Error.WriteLine("MSI not found: " + o.MsiPath);
                return 1;
            }

            using var inspector = new Inspector(o.MsiPath);
            foreach (var table in inspector.ListTables())
            {
                Console.Out.WriteLine(table);
            }
            return 0;
        }

        private static System.Collections.Generic.IEnumerable<string> FormatRowValues(
            System.Collections.Generic.IReadOnlyDictionary<string, object?> row)
        {
            foreach (var kv in row)
            {
                yield return kv.Value?.ToString() ?? string.Empty;
            }
        }
    }
}
