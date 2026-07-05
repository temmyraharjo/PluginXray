using System;
using System.Collections.Generic;

namespace PluginDebugger.Runtime
{
    /// <summary>What kind of object sits in <c>InputParameters["Target"]</c> for this message.</summary>
    public enum TargetKind
    {
        None = 0,
        Entity = 1,
        EntityReference = 2
    }

    /// <summary>Whether a discovered type is a plugin or a custom workflow activity (§4.12).</summary>
    public enum PluginTypeKind
    {
        Plugin = 0,
        WorkflowActivity = 1
    }

    /// <summary>Reflection info for one input/output argument of a custom workflow activity (FR-12.4/12.5).</summary>
    [Serializable]
    public sealed class WorkflowArgumentInfo
    {
        /// <summary>The property name — the key WorkflowInvoker uses.</summary>
        public string Name { get; set; }

        /// <summary>Friendly label from [Input]/[Output], falling back to <see cref="Name"/>.</summary>
        public string DisplayName { get; set; }

        /// <summary>"In", "Out", or "InOut".</summary>
        public string Direction { get; set; }

        /// <summary>The argument value type (T of InArgument&lt;T&gt;), simple name for the editor.</summary>
        public string TypeName { get; set; }

        public bool Required { get; set; }

        public bool IsInput => Direction == "In" || Direction == "InOut";
        public bool IsOutput => Direction == "Out" || Direction == "InOut";
    }

    /// <summary>A single input-argument value for a workflow activity run. Value carried as SDK-XML.</summary>
    [Serializable]
    public sealed class WorkflowArgumentDto
    {
        public string Name { get; set; }
        public string ValueXml { get; set; }   // SdkXml of the boxed value
        public string ValueType { get; set; }  // InputParameterType name, for display/diagnostics
    }

    /// <summary>A named image (pre or post) — the key matches how the plugin reads it.</summary>
    [Serializable]
    public sealed class ImageDto
    {
        public string Key { get; set; }
        public string EntityXml { get; set; } // DataContract XML of an Entity
    }

    /// <summary>A single SharedVariables entry. Value is carried as an SDK-XML string.</summary>
    [Serializable]
    public sealed class SharedVariableDto
    {
        public string Key { get; set; }
        public string ValueXml { get; set; }   // SdkXml of the boxed value
        public string ValueType { get; set; }  // assembly-qualified-ish hint for deserialization
    }

    /// <summary>
    /// A single arbitrary <c>InputParameters</c> entry beyond the message-driven <c>Target</c>
    /// (requirements FR-4.6). Value is carried as an SDK-XML string, exactly like SharedVariables.
    /// </summary>
    [Serializable]
    public sealed class InputParameterDto
    {
        public string Key { get; set; }
        public string ValueXml { get; set; }   // SdkXml of the boxed value
        public string ValueType { get; set; }  // InputParameterType name, for display/diagnostics
    }

    /// <summary>
    /// The synthesized execution context, expressed entirely as primitives + XML strings so it
    /// can cross into the child AppDomain without dragging non-[Serializable] SDK types along.
    /// </summary>
    [Serializable]
    public sealed class ContextDto
    {
        public string MessageName { get; set; }
        public string PrimaryEntityName { get; set; }
        public Guid PrimaryEntityId { get; set; }
        public int Stage { get; set; }
        public int Mode { get; set; }
        public int Depth { get; set; } = 1;
        public Guid UserId { get; set; }
        public Guid InitiatingUserId { get; set; }
        public Guid BusinessUnitId { get; set; }
        public Guid OrganizationId { get; set; }
        public string OrganizationName { get; set; }
        public Guid CorrelationId { get; set; }

        // Workflow-context fields (§4.12 / FR-12.3) — only meaningful for a CodeActivity run.
        public string StageName { get; set; }
        public int WorkflowCategory { get; set; }
        public int WorkflowMode { get; set; }

        public TargetKind TargetKind { get; set; }
        public string TargetXml { get; set; }

        /// <summary>
        /// Arbitrary InputParameters beyond <c>Target</c> (FR-4.6), keyed by parameter name. The
        /// reserved <c>Target</c> key is owned by the form-shape engine and never appears here.
        /// </summary>
        public List<InputParameterDto> InputParameters { get; set; } = new List<InputParameterDto>();

        /// <summary>For Create/40 (post): the Guid surfaced in OutputParameters["id"].</summary>
        public Guid? OutputId { get; set; }

        public List<ImageDto> PreImages { get; set; } = new List<ImageDto>();
        public List<ImageDto> PostImages { get; set; } = new List<ImageDto>();
        public List<SharedVariableDto> SharedVariables { get; set; } = new List<SharedVariableDto>();
    }

    /// <summary>Everything the child domain needs to load and run one plugin invocation.</summary>
    [Serializable]
    public sealed class RunRequest
    {
        /// <summary>The shadow copy of the plugin dll the child domain loads (original stays unlocked).</summary>
        public string ShadowDllPath { get; set; }

        /// <summary>Directory holding the shadow-copied plugin + its sibling dependencies.</summary>
        public string PluginDirectory { get; set; }

        /// <summary>The host (XrmToolBox) directory, where Microsoft.Xrm.Sdk and other host assemblies live.</summary>
        public string HostDirectory { get; set; }

        public string PluginTypeName { get; set; }
        public string UnsecureConfig { get; set; }
        public string SecureConfig { get; set; }

        /// <summary>Plugin vs custom workflow activity — selects the execution path (§4.12).</summary>
        public PluginTypeKind Kind { get; set; }

        /// <summary>Input-argument values for a workflow-activity run (empty for plugins).</summary>
        public List<WorkflowArgumentDto> InputArguments { get; set; } = new List<WorkflowArgumentDto>();

        public ExecutionMode Mode { get; set; }
        public ContextDto Context { get; set; }
    }

    /// <summary>A single OutputParameters entry, flattened for display.</summary>
    [Serializable]
    public sealed class OutputParameterDto
    {
        public string Key { get; set; }
        public string Display { get; set; }
    }

    /// <summary>The result of a run, carried back from the child domain (primitives only).</summary>
    [Serializable]
    public sealed class RunResult
    {
        public bool Success { get; set; }

        public bool ThrewInvalidPluginExecutionException { get; set; }
        public string ExceptionType { get; set; }
        public string ExceptionMessage { get; set; }
        public string ExceptionStackTrace { get; set; }

        public List<OutputParameterDto> OutputParameters { get; set; } = new List<OutputParameterDto>();
        public string OutputParametersXml { get; set; }
    }

    /// <summary>Reflection summary of one IPlugin type or workflow activity found in an assembly.</summary>
    [Serializable]
    public sealed class PluginTypeInfo
    {
        public string FullName { get; set; }

        /// <summary>Plugin or custom workflow activity (§4.12).</summary>
        public PluginTypeKind Kind { get; set; }

        /// <summary>Has a public (string unsecure, string secure) constructor.</summary>
        public bool HasConfigCtor { get; set; }

        /// <summary>Has a public (string unsecure) constructor.</summary>
        public bool HasUnsecureCtor { get; set; }

        /// <summary>Has a public parameterless constructor.</summary>
        public bool HasDefaultCtor { get; set; }

        /// <summary>For a workflow activity: its reflected input/output arguments (FR-12.4/12.5).</summary>
        public List<WorkflowArgumentInfo> Arguments { get; set; } = new List<WorkflowArgumentInfo>();

        public override string ToString() =>
            Kind == PluginTypeKind.WorkflowActivity ? FullName + "  [workflow]" : FullName;
    }
}
