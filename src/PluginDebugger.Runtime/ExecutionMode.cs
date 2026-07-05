namespace PluginDebugger.Runtime
{
    /// <summary>
    /// Execution-mode policy enforced by the <see cref="ServiceWrapper"/> (requirements §4.7, OD-2).
    /// </summary>
    public enum ExecutionMode
    {
        /// <summary>
        /// Default (OD-2). All SDK requests go to the live environment; a run in this mode
        /// requires the per-run write confirmation (requirements §4.1.4 / FR-7.1).
        /// </summary>
        FullReal = 0,

        /// <summary>
        /// Retrieve / RetrieveMultiple / query requests hit the live environment;
        /// Create / Update / Delete / Associate / etc. are intercepted, logged, and NOT executed.
        /// </summary>
        ReadRealWriteMock = 1,

        /// <summary>All requests are intercepted and logged; reads return empty results.</summary>
        FullMock = 2
    }
}
