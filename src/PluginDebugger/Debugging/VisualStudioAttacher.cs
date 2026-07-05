using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using PluginDebugger.Runtime;

namespace PluginDebugger.Debugging
{
    /// <summary>A running Visual Studio instance discovered in the Running Object Table.</summary>
    internal sealed class VsInstance
    {
        public int ProcessId { get; set; }
        public string Version { get; set; }
        public string Caption { get; set; }

        /// <summary>The live EnvDTE.DTE COM object (used late-bound via <c>dynamic</c>).</summary>
        public object Dte { get; set; }

        public override string ToString()
        {
            var product = VsMonikerParser.ProductName(Version);
            return string.IsNullOrEmpty(Caption)
                ? $"{product} — pid {ProcessId}"
                : $"{product} — {Caption} (pid {ProcessId})";
        }
    }

    /// <summary>
    /// Enumerates running Visual Studio instances and attaches the chosen one to a process
    /// (requirements §4.9). The plugin runs in-process in a child AppDomain, so the correct
    /// attach target is the XrmToolBox host process itself (FR-9.2). EnvDTE is used late-bound
    /// so no version-specific PIA reference is needed; ROT enumeration naturally finds every
    /// installed VS version (FR-9.3).
    /// </summary>
    internal static class VisualStudioAttacher
    {
        public static List<VsInstance> GetRunningInstances()
        {
            var instances = new List<VsInstance>();
            MessageFilter.Register();
            try
            {
                if (GetRunningObjectTable(0, out var rot) != 0 || rot == null)
                {
                    return instances;
                }

                rot.EnumRunning(out var enumMoniker);
                CreateBindCtx(0, out var bindCtx);

                var monikers = new IMoniker[1];
                enumMoniker.Reset();
                while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    try
                    {
                        monikers[0].GetDisplayName(bindCtx, null, out var displayName);
                        if (!VsMonikerParser.TryParse(displayName, out var version, out var pid))
                        {
                            continue;
                        }

                        rot.GetObject(monikers[0], out var dte);
                        instances.Add(new VsInstance
                        {
                            ProcessId = pid,
                            Version = version,
                            Caption = TryGetCaption(dte),
                            Dte = dte
                        });
                    }
                    catch
                    {
                        // A single uncooperative ROT entry shouldn't abort enumeration.
                    }
                }
            }
            finally
            {
                MessageFilter.Revoke();
            }

            return instances;
        }

        /// <summary>Attaches the selected VS instance to <paramref name="processId"/> (the XrmToolBox host).</summary>
        public static void Attach(VsInstance instance, int processId)
        {
            if (instance?.Dte == null)
            {
                throw new InvalidOperationException("No Visual Studio instance selected.");
            }

            MessageFilter.Register();
            try
            {
                dynamic dte = instance.Dte;
                dynamic localProcesses = dte.Debugger.LocalProcesses;
                foreach (dynamic process in localProcesses)
                {
                    if ((int)process.ProcessID == processId)
                    {
                        process.Attach();
                        return;
                    }
                }

                throw new InvalidOperationException(
                    $"Process {processId} was not found in {VsMonikerParser.ProductName(instance.Version)}'s local process list. " +
                    "It may need to be run elevated to match XrmToolBox.");
            }
            finally
            {
                MessageFilter.Revoke();
            }
        }

        private static string TryGetCaption(object dteObject)
        {
            dynamic dte = dteObject;
            try
            {
                string solutionPath = dte.Solution?.FullName;
                if (!string.IsNullOrEmpty(solutionPath))
                {
                    return Path.GetFileNameWithoutExtension(solutionPath);
                }
            }
            catch
            {
                // fall through to main-window caption
            }

            try
            {
                return dte.MainWindow.Caption;
            }
            catch
            {
                return null;
            }
        }

        [DllImport("ole32.dll")]
        private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);

        [DllImport("ole32.dll")]
        private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);
    }
}
