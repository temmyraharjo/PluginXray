using System;
using Microsoft.Xrm.Sdk.Metadata;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// The kind of type-appropriate input an attribute needs in the typed editor (requirements §4.5).
    /// </summary>
    public enum AttributeEditorKind
    {
        String,
        Memo,
        Boolean,
        WholeNumber,            // Integer
        BigInt,                 // BigInt
        Decimal,
        Double,
        Money,
        DateTime,
        OptionSet,              // Picklist / State / Status
        MultiSelectOptionSet,
        Guid,                   // Uniqueidentifier
        Lookup                  // Lookup / Customer / Owner (Customer/Owner are polymorphic)
    }

    /// <summary>
    /// Maps Dataverse attribute metadata to an <see cref="AttributeEditorKind"/>, and answers
    /// whether a kind is "ambiguous" for JSON purposes (FR-5.5 / OD-3): a genuinely unambiguous
    /// scalar can be read from a bare JSON value alone, whereas an ambiguous kind (e.g. a bare
    /// number could be int / decimal / double) needs table metadata to pin its exact type. See
    /// <see cref="AttributeJson"/> for the CRM/Dataverse SDK object shapes used on the wire.
    /// </summary>
    public static class AttributeTypeMapper
    {
        /// <summary>
        /// Resolves the editor kind from full metadata. Returns null for attributes with no
        /// usable type (e.g. virtual base attributes, calculated-only, partylist for v1).
        /// </summary>
        public static AttributeEditorKind? FromMetadata(AttributeMetadata attribute)
        {
            if (attribute == null)
            {
                return null;
            }

            // Concrete-type checks first (the type code alone is ambiguous for these).
            if (attribute is MultiSelectPicklistAttributeMetadata)
            {
                return AttributeEditorKind.MultiSelectOptionSet;
            }

            if (!attribute.AttributeType.HasValue)
            {
                return null;
            }

            return FromTypeCode(attribute.AttributeType.Value);
        }

        /// <summary>Resolves the editor kind from the attribute type code alone (unit-testable).</summary>
        public static AttributeEditorKind? FromTypeCode(AttributeTypeCode code)
        {
            switch (code)
            {
                case AttributeTypeCode.String: return AttributeEditorKind.String;
                case AttributeTypeCode.Memo: return AttributeEditorKind.Memo;
                case AttributeTypeCode.Boolean: return AttributeEditorKind.Boolean;
                case AttributeTypeCode.Integer: return AttributeEditorKind.WholeNumber;
                case AttributeTypeCode.BigInt: return AttributeEditorKind.BigInt;
                case AttributeTypeCode.Decimal: return AttributeEditorKind.Decimal;
                case AttributeTypeCode.Double: return AttributeEditorKind.Double;
                case AttributeTypeCode.Money: return AttributeEditorKind.Money;
                case AttributeTypeCode.DateTime: return AttributeEditorKind.DateTime;
                case AttributeTypeCode.Picklist:
                case AttributeTypeCode.State:
                case AttributeTypeCode.Status: return AttributeEditorKind.OptionSet;
                case AttributeTypeCode.Uniqueidentifier: return AttributeEditorKind.Guid;
                case AttributeTypeCode.Lookup:
                case AttributeTypeCode.Customer:
                case AttributeTypeCode.Owner: return AttributeEditorKind.Lookup;
                default: return null; // Virtual, EntityName, ManagedProperty, PartyList, CalendarRules, etc.
            }
        }

        /// <summary>
        /// Ambiguous kinds need table metadata to pin their exact type from a shared JSON
        /// representation; the rest can be read from a bare JSON value alone. A plain number could
        /// be an int, a decimal or a double — so only the genuinely unambiguous scalars are
        /// resolvable without metadata.
        /// </summary>
        public static bool IsAmbiguous(AttributeEditorKind kind)
        {
            switch (kind)
            {
                case AttributeEditorKind.String:
                case AttributeEditorKind.Memo:
                case AttributeEditorKind.Boolean:
                case AttributeEditorKind.WholeNumber:
                case AttributeEditorKind.BigInt:
                    return false;
                default:
                    return true;
            }
        }
    }
}
