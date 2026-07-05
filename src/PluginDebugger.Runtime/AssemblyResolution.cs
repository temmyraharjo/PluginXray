using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// Shared child-AppDomain assembly resolution (requirements §4.8.6). The child domain's
    /// ApplicationBase is the folder holding PluginDebugger.Runtime.dll, so this handler must
    /// also locate:
    ///   - host assemblies (Microsoft.Xrm.Sdk, Newtonsoft.Json, ...) from the host directory, and
    ///   - the plugin's own private dependencies from its (shadow-copied) folder.
    ///
    /// Host assemblies are resolved from the host directory (the SAME files the main domain loaded)
    /// so their type identity is unified across the boundary — without that, SDK objects and MBRO
    /// proxies cannot cross.
    /// </summary>
    public static class AssemblyResolution
    {
        public static Assembly Resolve(string fullName, string hostDirectory, string pluginDirectory)
        {
            var simpleName = new AssemblyName(fullName).Name;

            // 1) Already loaded in this domain — return it so identity stays unified.
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
            if (loaded != null)
            {
                return loaded;
            }

            // 2) Host directory first (SDK / framework / Newtonsoft) so we match the main domain's copy.
            var fromHost = TryLoadFrom(hostDirectory, simpleName);
            if (fromHost != null)
            {
                return fromHost;
            }

            // 3) The plugin's own private dependencies.
            return TryLoadFrom(pluginDirectory, simpleName);
        }

        private static Assembly TryLoadFrom(string directory, string simpleName)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            var candidate = Path.Combine(directory, simpleName + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        }
    }
}
