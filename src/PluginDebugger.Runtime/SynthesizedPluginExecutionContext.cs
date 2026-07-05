using System;
using Microsoft.Xrm.Sdk;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// A hand-built <see cref="IPluginExecutionContext"/>. Instantiated INSIDE the child
    /// AppDomain (it is a plain object, not marshaled) and populated from a <see cref="ContextDto"/>.
    ///
    /// Implements the full <see cref="IExecutionContext"/> surface so plugins that read any
    /// context property behave as they would on the platform. Fidelity caveats (no real
    /// transaction, depth/loop guard, or impersonation) are surfaced by the UI, per §4.7.4 —
    /// the harness does not pretend otherwise.
    /// </summary>
    public sealed class SynthesizedPluginExecutionContext : IPluginExecutionContext
    {
        public int Stage { get; set; }
        public IPluginExecutionContext ParentContext { get; set; }

        public int Mode { get; set; }
        public int IsolationMode { get; set; } = 1; // sandbox
        public int Depth { get; set; } = 1;
        public string MessageName { get; set; }
        public string PrimaryEntityName { get; set; }
        public Guid? RequestId { get; set; }
        public string SecondaryEntityName { get; set; }
        public ParameterCollection InputParameters { get; set; } = new ParameterCollection();
        public ParameterCollection OutputParameters { get; set; } = new ParameterCollection();
        public ParameterCollection SharedVariables { get; set; } = new ParameterCollection();
        public Guid UserId { get; set; }
        public Guid InitiatingUserId { get; set; }
        public Guid BusinessUnitId { get; set; }
        public Guid OrganizationId { get; set; }
        public string OrganizationName { get; set; }
        public Guid PrimaryEntityId { get; set; }
        public EntityImageCollection PreEntityImages { get; set; } = new EntityImageCollection();
        public EntityImageCollection PostEntityImages { get; set; } = new EntityImageCollection();
        public Guid CorrelationId { get; set; }
        public bool IsExecutingOffline { get; set; }
        public bool IsOfflinePlayback { get; set; }
        public bool IsInTransaction { get; set; }
        public Guid OperationId { get; set; }
        public DateTime OperationCreatedOn { get; set; } = DateTime.UtcNow;
        public EntityReference OwningExtension { get; set; }
    }
}
