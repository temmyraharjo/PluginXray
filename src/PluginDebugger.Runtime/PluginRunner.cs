using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xrm.Sdk;

namespace PluginDebugger.Runtime
{
    /// <summary>Carries the run result plus the per-run symbol indicator (§4.8.5).</summary>
    public sealed class RunOutcome
    {
        public RunResult Result { get; set; }

        /// <summary>True when a matching .pdb was found and copied alongside the shadow dll.</summary>
        public bool SymbolsLoaded { get; set; }
    }

    /// <summary>
    /// MAIN-domain orchestrator for the load/run loop (requirements §4.8). For every run it:
    ///   1. copies the plugin dll + .pdb + sibling dependencies to a fresh temp folder,
    ///      so the original build output is never locked (the developer can rebuild in VS),
    ///   2. spins up a fresh child AppDomain (shadow-copy enabled) — one per run, so each run
    ///      loads the latest build,
    ///   3. runs the plugin via <see cref="PluginExecutor"/> in that domain, and
    ///   4. unloads the domain and deletes the temp folder, releasing every lock.
    ///
    /// The .pdb is copied next to the shadow dll so breakpoints bind, and its presence is
    /// reported back as <see cref="RunOutcome.SymbolsLoaded"/>.
    /// </summary>
    public static class PluginRunner
    {
        static PluginRunner()
        {
            // XrmToolBox loads PluginDebugger.Runtime.dll via Assembly.LoadFrom, which puts it in
            // the "LoadFrom" binding context. When the child domain marshals an AssemblyInspector /
            // PluginExecutor proxy back here, the main-domain binder tries to resolve
            // "PluginDebugger.Runtime" by name and DOESN'T look in the LoadFrom context — so the
            // proxy cast fails ("Unable to cast transparent proxy"). Hand back the already-loaded
            // assembly so the cast binds to the same type identity.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveRuntimeInMainDomain;
        }

        private static Assembly ResolveRuntimeInMainDomain(object sender, ResolveEventArgs args)
        {
            var simpleName = new AssemblyName(args.Name).Name;
            return simpleName.Equals("PluginDebugger.Runtime", StringComparison.OrdinalIgnoreCase)
                ? typeof(PluginRunner).Assembly
                : null;
        }

        private static string RootTempDir =>
            Path.Combine(Path.GetTempPath(), "PluginDebugger");

        public static PluginTypeInfo[] ListPluginTypes(string dllPath)
        {
            if (!File.Exists(dllPath))
            {
                throw new FileNotFoundException("Plugin assembly not found.", dllPath);
            }

            var tempDir = CreateRunDir("inspect");
            try
            {
                var shadowDll = CopyAssemblyWithDependencies(dllPath, tempDir);
                var domain = CreateChildDomain("PluginDebugger.Inspect");
                try
                {
                    var inspector = (AssemblyInspector)domain.CreateInstanceAndUnwrap(
                        typeof(AssemblyInspector).Assembly.FullName,
                        typeof(AssemblyInspector).FullName);
                    return inspector.GetPluginTypes(shadowDll, HostDirectory);
                }
                finally
                {
                    AppDomain.Unload(domain);
                }
            }
            finally
            {
                TryDeleteDir(tempDir);
            }
        }

        public static RunOutcome Run(
            string dllPath,
            RunRequest requestTemplate,
            IOrganizationService realService,
            RunLogSink logSink)
        {
            if (!File.Exists(dllPath))
            {
                throw new FileNotFoundException("Plugin assembly not found.", dllPath);
            }

            var tempDir = CreateRunDir("run");
            AppDomain domain = null;
            try
            {
                var shadowDll = CopyAssemblyWithDependencies(dllPath, tempDir);
                var pdbCopied = CopySymbols(dllPath, tempDir);

                // Fill in the paths that are only known once we've shadow-copied.
                requestTemplate.ShadowDllPath = shadowDll;
                requestTemplate.PluginDirectory = tempDir;
                requestTemplate.HostDirectory = HostDirectory;

                var bridge = new ServiceBridge(realService, requestTemplate.Mode, logSink);

                domain = CreateChildDomain("PluginDebugger.Run." + Guid.NewGuid().ToString("N"));
                var executor = (PluginExecutor)domain.CreateInstanceAndUnwrap(
                    typeof(PluginExecutor).Assembly.FullName,
                    typeof(PluginExecutor).FullName);

                // Install the child-domain resolver FIRST, via an SDK-free string-only call, so the
                // SDK is resolvable when the CLR JIT-compiles Execute (which references SDK types).
                executor.PrepareResolver(requestTemplate.PluginDirectory, requestTemplate.HostDirectory);

                var result = requestTemplate.Kind == PluginTypeKind.WorkflowActivity
                    ? executor.ExecuteWorkflow(requestTemplate, bridge, logSink)
                    : executor.Execute(requestTemplate, bridge, logSink);

                return new RunOutcome { Result = result, SymbolsLoaded = pdbCopied };
            }
            finally
            {
                if (domain != null)
                {
                    AppDomain.Unload(domain); // releases the LoadFrom lock on the shadow dll
                }
                TryDeleteDir(tempDir);
            }
        }

        // ---- helpers -----------------------------------------------------------------------

        /// <summary>
        /// The directory the host (XrmToolBox) provides the SDK from. Derived from the folder the
        /// main domain actually loaded <c>Microsoft.Xrm.Sdk</c> from — more reliable than the app
        /// base dir if the host keeps the SDK in a sub-folder — with the base dir as a fallback.
        /// </summary>
        private static string HostDirectory
        {
            get
            {
                try
                {
                    var sdkLocation = typeof(Entity).Assembly.Location;
                    if (!string.IsNullOrEmpty(sdkLocation) && File.Exists(sdkLocation))
                    {
                        return Path.GetDirectoryName(sdkLocation);
                    }
                }
                catch
                {
                    // Fall through to the app base dir.
                }

                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        /// <summary>The directory PluginDebugger.Runtime.dll is loaded from (under XrmToolBox: the Plugins folder).</summary>
        private static string RuntimeDirectory
        {
            get
            {
                var assembly = typeof(PluginRunner).Assembly;
                var location = assembly.Location;
                if (string.IsNullOrEmpty(location))
                {
                    location = new Uri(assembly.CodeBase).LocalPath;
                }
                return Path.GetDirectoryName(location);
            }
        }

        private static AppDomain CreateChildDomain(string name)
        {
            // ApplicationBase must be the folder that holds PluginDebugger.Runtime.dll so the child
            // domain can load it (under XrmToolBox that's the Plugins folder, NOT the host exe dir).
            // Host assemblies (Microsoft.Xrm.Sdk, Newtonsoft, ...) are pulled from HostDirectory by
            // the child-domain AssemblyResolve handlers, which also unifies their identity with the
            // main domain — a precondition for marshaling SDK objects / MBRO proxies across the boundary.
            // ShadowCopyFiles keeps the loaded assemblies unlocked.
            var setup = new AppDomainSetup
            {
                ApplicationBase = RuntimeDirectory,
                ShadowCopyFiles = "true"
            };

            return AppDomain.CreateDomain(name, AppDomain.CurrentDomain.Evidence, setup);
        }

        private static string CreateRunDir(string prefix)
        {
            var dir = Path.Combine(RootTempDir, prefix + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Copies the target dll and its sibling .dll dependencies into <paramref name="targetDir"/>
        /// and returns the path of the copied target dll. Sibling pdbs are copied too so any
        /// dependency that gets stepped into also has symbols.
        /// </summary>
        private static string CopyAssemblyWithDependencies(string dllPath, string targetDir)
        {
            var sourceDir = Path.GetDirectoryName(dllPath) ?? ".";
            var primaryName = Path.GetFileName(dllPath);
            var hostAssemblies = HostAssemblyNames();

            foreach (var file in Directory.GetFiles(sourceDir, "*.dll").Concat(Directory.GetFiles(sourceDir, "*.pdb")))
            {
                var name = Path.GetFileName(file);

                // Do NOT shadow-copy the host-provided SDK assemblies (Microsoft.Xrm.Sdk,
                // Microsoft.Crm.Sdk.Proxy, ...) even when a plugin bundles its own copy — e.g. a
                // plugin *package* project. If they sat next to the plugin in the shadow folder,
                // Assembly.LoadFrom would bind the plugin's copy (LoadFrom probes the loaded
                // assembly's own directory) BEFORE our host-first AssemblyResolve handler runs,
                // splitting the SDK's type identity from the main domain and breaking marshaling /
                // dependency resolution. Leaving them out forces the child domain to resolve them
                // from the host directory, the SAME copy the main domain uses.
                if (!string.Equals(name, primaryName, StringComparison.OrdinalIgnoreCase)
                    && IsHostProvidedSdk(name, hostAssemblies))
                {
                    continue;
                }

                var dest = Path.Combine(targetDir, name);
                try
                {
                    File.Copy(file, dest, overwrite: true);
                }
                catch (IOException)
                {
                    // A locked sibling shouldn't abort the run; the resolver will fall back.
                }
            }

            return Path.Combine(targetDir, primaryName);
        }

        /// <summary>The simple names of every assembly the host directory provides.</summary>
        private static HashSet<string> HostAssemblyNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var host = HostDirectory;
                if (!string.IsNullOrEmpty(host) && Directory.Exists(host))
                {
                    foreach (var dll in Directory.GetFiles(host, "*.dll"))
                    {
                        names.Add(Path.GetFileNameWithoutExtension(dll));
                    }
                }
            }
            catch
            {
                // If we can't enumerate the host dir, fall back to copying everything (old behaviour).
            }
            return names;
        }

        /// <summary>
        /// True for an SDK assembly the host provides (<c>Microsoft.Xrm.*</c> / <c>Microsoft.Crm.*</c>).
        /// Scoped to the SDK so a plugin's other private dependencies (Newtonsoft, RestSharp, …) are
        /// still shadow-copied and used as the plugin ships them.
        /// </summary>
        private static bool IsHostProvidedSdk(string fileName, HashSet<string> hostAssemblies)
        {
            var simple = Path.GetFileNameWithoutExtension(fileName);
            var isSdk = simple.StartsWith("Microsoft.Xrm.", StringComparison.OrdinalIgnoreCase)
                        || simple.StartsWith("Microsoft.Crm.", StringComparison.OrdinalIgnoreCase);
            return isSdk && hostAssemblies.Contains(simple);
        }

        private static bool CopySymbols(string dllPath, string targetDir)
        {
            var pdbSource = Path.ChangeExtension(dllPath, ".pdb");
            if (!File.Exists(pdbSource))
            {
                return false;
            }

            var pdbDest = Path.Combine(targetDir, Path.GetFileName(pdbSource));
            try
            {
                File.Copy(pdbSource, pdbDest, overwrite: true);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static void TryDeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Temp cleanup is best-effort; a leftover folder under %TEMP% is harmless.
            }
        }
    }
}
