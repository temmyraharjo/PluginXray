using System;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// Parses Running Object Table display names for Visual Studio's automation object
    /// (requirements §4.9). Names look like <c>!VisualStudio.DTE.17.0:12345</c> — the trailing
    /// number is the VS process id, and the version (17.0 = 2022, 16.0 = 2019) drives the
    /// product label. Pure string logic so it is unit-testable without COM.
    /// </summary>
    public static class VsMonikerParser
    {
        public static bool TryParse(string displayName, out string version, out int processId)
        {
            version = null;
            processId = 0;

            if (string.IsNullOrEmpty(displayName))
            {
                return false;
            }

            var name = displayName.TrimStart('!');
            if (!name.StartsWith("VisualStudio.DTE", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var colon = name.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(name.Substring(colon + 1), out processId) || processId <= 0)
            {
                return false;
            }

            var left = name.Substring(0, colon); // e.g. VisualStudio.DTE.17.0
            var dteMarker = left.IndexOf("DTE.", StringComparison.OrdinalIgnoreCase);
            version = dteMarker >= 0 ? left.Substring(dteMarker + 4) : null;
            return true;
        }

        public static string ProductName(string version)
        {
            switch (version)
            {
                case "18.0": return "Visual Studio 2026";
                case "17.0": return "Visual Studio 2022";
                case "16.0": return "Visual Studio 2019";
                case "15.0": return "Visual Studio 2017";
                default: return string.IsNullOrEmpty(version) ? "Visual Studio" : "Visual Studio (DTE " + version + ")";
            }
        }
    }
}
