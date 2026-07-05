using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Workflow;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// A hand-built <see cref="IWorkflowContext"/> handed to a custom workflow activity (code
    /// activity) as an extension (requirements §4.12 / FR-12.3). Instantiated INSIDE the child
    /// AppDomain and populated from a <see cref="ContextDto"/>. Mirrors
    /// <see cref="SynthesizedPluginExecutionContext"/> but for the workflow surface: it adds
    /// <see cref="StageName"/>, <see cref="WorkflowCategory"/>, and <see cref="WorkflowMode"/>.
    /// </summary>
    public sealed class SynthesizedWorkflowContext : IWorkflowContext
    {
        // IWorkflowContext-specific
        public IWorkflowContext ParentContext { get; set; }
        public string StageName { get; set; }
        public int WorkflowCategory { get; set; }
        public int WorkflowMode { get; set; }

        // IExecutionContext
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
        public EntityReference OwningExtension { get; set; }
        public Guid CorrelationId { get; set; }
        public bool IsExecutingOffline { get; set; }
        public bool IsOfflinePlayback { get; set; }
        public bool IsInTransaction { get; set; }
        public Guid OperationId { get; set; }
        public DateTime OperationCreatedOn { get; set; } = DateTime.UtcNow;
    }
}
