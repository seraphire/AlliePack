using System;
using System.Collections.Generic;
using System.Linq;
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

        // Further features that list the same file at the same destination.  Duplicates
        // are merged into one component referenced by every feature, so the file installs
        // when any of them is selected.  Always empty when WixFeature is null: a file in
        // the base structure is installed regardless, and the base wins.
        internal List<Feature> ExtraFeatures { get; } = new List<Feature>();

        /// <summary>
        /// Folds the feature membership of a dropped duplicate into this file.
        /// </summary>
        internal void MergeFeaturesFrom(ResolvedFile dropped)
        {
            if (WixFeature == null) return;              // already base: always installed
            if (dropped.WixFeature == null)
            {
                WixFeature = null;                       // base wins
                ExtraFeatures.Clear();
                return;
            }
            foreach (var f in new[] { dropped.WixFeature }.Concat(dropped.ExtraFeatures))
                if (f != WixFeature && !ExtraFeatures.Contains(f))
                    ExtraFeatures.Add(f);
        }

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
