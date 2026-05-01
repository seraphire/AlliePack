using System;
using WixSharp;

namespace AlliePack
{
    public class ResolvedFile
    {
        public string SourcePath { get; set; } = string.Empty;
        public string RelativeDestinationPath { get; set; } = string.Empty;

        // When non-null, files are scoped to this WiX feature.
        // Null means they belong to the default/base feature.
        public Feature? WixFeature { get; set; }

        public override bool Equals(object? obj)
        {
            if (obj is ResolvedFile other)
            {
                return SourcePath == other.SourcePath && RelativeDestinationPath == other.RelativeDestinationPath;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return (SourcePath + RelativeDestinationPath).GetHashCode();
        }
    }
}
