using System;

namespace AlliePack
{
    public class ResolvedFile
    {
        public string SourcePath { get; set; } = string.Empty;
        public string RelativeDestinationPath { get; set; } = string.Empty;

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
