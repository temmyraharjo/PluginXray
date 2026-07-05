using System;
using System.Collections.Generic;
using System.Linq;

namespace PluginDebugger.Runtime
{
    /// <summary>What kind of editor the <c>Target</c> needs for a given message.</summary>
    public enum TargetEditorKind
    {
        None,
        /// <summary>An <c>Entity</c> built from a typed attribute editor (Create / Update).</summary>
        EntityAttributes,
        /// <summary>An <c>EntityReference</c> chosen with a record picker (Delete).</summary>
        EntityReference
    }

    /// <summary>
    /// The resolved shape of the form for one MessageName + Stage selection (requirements §4.3).
    /// This is the immutable description the UI binds to: which Target editor to show, which
    /// image panels to enable, and which extra fields to expose. It encodes the v1 form-shape
    /// matrix so impossible contexts (e.g. a PostImage on a pre-operation stage) simply cannot
    /// be constructed — the corresponding control is disabled rather than warned about (FR-3.3).
    /// </summary>
    public sealed class FormShape
    {
        internal FormShape(
            string message,
            int stage,
            TargetEditorKind targetEditor,
            bool targetIsChangedAttributesOnly,
            bool preImageAllowed,
            bool postImageAllowed,
            bool exposesOutputId,
            string targetLabel)
        {
            Message = message;
            Stage = stage;
            TargetEditor = targetEditor;
            TargetIsChangedAttributesOnly = targetIsChangedAttributesOnly;
            PreImageAllowed = preImageAllowed;
            PostImageAllowed = postImageAllowed;
            ExposesOutputId = exposesOutputId;
            TargetLabel = targetLabel;
        }

        public string Message { get; }
        public int Stage { get; }
        public bool IsPostOperation => Stage == 40;

        public TargetEditorKind TargetEditor { get; }

        /// <summary>
        /// True for Update: the Target carries ONLY changed attributes, so the editor starts
        /// empty and the user adds just what they intend to change (FR-3.4) — keeping
        /// <c>InputParameters.Contains("x")</c> faithful to production.
        /// </summary>
        public bool TargetIsChangedAttributesOnly { get; }

        public bool PreImageAllowed { get; }
        public bool PostImageAllowed { get; }

        /// <summary>True for Create / post-operation: <c>OutputParameters["id"]</c> is exposed.</summary>
        public bool ExposesOutputId { get; }

        /// <summary>Human-readable label for the Target editor section.</summary>
        public string TargetLabel { get; }
    }

    /// <summary>
    /// Resolves a <see cref="FormShape"/> from MessageName + Stage. The single source of truth
    /// for the v1 (Create / Update / Delete) matrix (requirements §4.3, OD-1). Pure logic — no
    /// UI or SDK dependencies — so it is unit-testable on its own.
    /// </summary>
    public static class FormShapeEngine
    {
        public static readonly IReadOnlyList<string> SupportedMessages = new[] { "Create", "Update", "Delete" };

        /// <summary>10 = pre-validation, 20 = pre-operation, 40 = post-operation.</summary>
        public static readonly IReadOnlyList<int> SupportedStages = new[] { 10, 20, 40 };

        public static bool IsSupported(string message, int stage) =>
            message != null
            && SupportedMessages.Contains(message, StringComparer.OrdinalIgnoreCase)
            && SupportedStages.Contains(stage);

        public static FormShape Resolve(string message, int stage)
        {
            if (!IsSupported(message, stage))
            {
                throw new ArgumentException($"Unsupported message/stage combination: '{message}' / {stage}.");
            }

            bool isPost = stage == 40;

            if (message.Equals("Create", StringComparison.OrdinalIgnoreCase))
            {
                return new FormShape(
                    "Create", stage,
                    TargetEditorKind.EntityAttributes,
                    targetIsChangedAttributesOnly: false,
                    preImageAllowed: false,            // a record does not exist before Create
                    postImageAllowed: isPost,          // available only post-operation
                    exposesOutputId: isPost,           // OutputParameters["id"] after the create
                    targetLabel: "Target attributes (Entity)");
            }

            if (message.Equals("Update", StringComparison.OrdinalIgnoreCase))
            {
                return new FormShape(
                    "Update", stage,
                    TargetEditorKind.EntityAttributes,
                    targetIsChangedAttributesOnly: true,
                    preImageAllowed: true,             // the prior state exists
                    postImageAllowed: isPost,
                    exposesOutputId: false,
                    targetLabel: "Target — changed attributes only (Entity)");
            }

            // Delete
            return new FormShape(
                "Delete", stage,
                TargetEditorKind.EntityReference,
                targetIsChangedAttributesOnly: false,
                preImageAllowed: true,                 // the record being deleted exists
                postImageAllowed: false,               // nothing remains afterwards
                exposesOutputId: false,
                targetLabel: "Target record (EntityReference)");
        }

        /// <summary>
        /// A permissive shape for an arbitrary ("Other") message name the v1 matrix does not model
        /// — e.g. a custom action, SetState, Assign. There is no way to know the message's real
        /// contract, so the Entity-attribute Target and both pre/post images are exposed for the
        /// user to fill freely. OutputParameters are intentionally NOT auto-exposed: they are the
        /// developer's domain to define. Use for messages outside <see cref="SupportedMessages"/>.
        /// </summary>
        public static FormShape General(string message, int stage)
        {
            return new FormShape(
                message ?? string.Empty, stage,
                TargetEditorKind.EntityAttributes,
                targetIsChangedAttributesOnly: true,   // start empty; the user adds what they need
                preImageAllowed: true,
                postImageAllowed: true,
                exposesOutputId: false,                // developer defines OutputParameters themselves
                targetLabel: "Target attributes (Entity) — custom message");
        }
    }
}
