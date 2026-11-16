using System;
using System.Collections.Generic;
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

        // The sign: of the nearest structure entry, or null to let signing.files.mode decide.
        internal FileSignMode? SignMode { get; set; }

        // sign: of entries that reached the same destination with the same file and
        // were dropped as duplicates; the signing plan still weighs them.
        internal List<FileSignMode?> DuplicateSignModes { get; } = new List<FileSignMode?>();

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
