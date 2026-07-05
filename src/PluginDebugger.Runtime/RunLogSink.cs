using System;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// A log entry surfaced to the UI. Carries the wall-clock time it was raised.
    /// </summary>
    public sealed class LogEntry : EventArgs
    {
        public LogEntry(LogCategory category, string message)
        {
            Category = category;
            Message = message ?? string.Empty;
            Timestamp = DateTime.Now;
        }

        public LogCategory Category { get; }
        public string Message { get; }
        public DateTime Timestamp { get; }
    }

    /// <summary>
    /// MAIN-domain log sink. The child AppDomain receives a transparent proxy to this
    /// instance (it derives from <see cref="MarshalByRefObject"/>) and every plugin
    /// trace / SDK call marshals back here, where the UI is subscribed via
    /// <see cref="EntryLogged"/>.
    /// </summary>
    public sealed class RunLogSink : MarshalByRefObject, ILogSink
    {
        /// <summary>Raised (on a worker thread) whenever the child domain logs an entry.</summary>
        public event EventHandler<LogEntry> EntryLogged;

        public void Log(LogCategory category, string message)
        {
            EntryLogged?.Invoke(this, new LogEntry(category, message));
        }

        /// <summary>Keep the proxy alive for the lifetime of the host (no lease expiry).</summary>
        public override object InitializeLifetimeService() => null;
    }
}
