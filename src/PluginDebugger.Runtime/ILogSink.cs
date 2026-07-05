using System;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// Severity / channel for a run-log entry. Kept deliberately small so the UI can
    /// colour-code without knowing about the internals.
    /// </summary>
    public enum LogCategory
    {
        Info,
        Trace,      // plugin ITracingService output
        SdkReal,    // an SDK request that was executed against the live environment
        SdkMock,    // an SDK request that was intercepted / not executed
        Warning,
        Error
    }

    /// <summary>
    /// Sink for run-log entries. The concrete implementation (<see cref="RunLogSink"/>)
    /// lives in the MAIN AppDomain as a <see cref="MarshalByRefObject"/>; the child
    /// domain holds a transparent proxy and calls back across the boundary.
    /// </summary>
    public interface ILogSink
    {
        void Log(LogCategory category, string message);
    }
}
