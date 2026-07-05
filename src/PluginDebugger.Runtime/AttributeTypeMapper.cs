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
    /// whether a kind is "ambiguous" for JSON purposes (FR-5.5 / OD-3): unambiguous scalars may
    /// be written as plain JSON; ambiguous kinds require a typed envelope.
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
        /// Ambiguous kinds require a JSON typed envelope; the rest may be written as a plain
        /// scalar. A plain number could be an int, an optionset, a money or a decimal — so only
        /// the genuinely unambiguous scalars are allowed bare.
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

        /// <summary>The short token used in the JSON typed envelope ("t" field).</summary>
        public static string EnvelopeToken(AttributeEditorKind kind)
        {
            switch (kind)
            {
                case AttributeEditorKind.Decimal: return "decimal";
                case AttributeEditorKind.Double: return "double";
                case AttributeEditorKind.Money: return "money";
                case AttributeEditorKind.DateTime: return "datetime";
                case AttributeEditorKind.OptionSet: return "optionset";
                case AttributeEditorKind.MultiSelectOptionSet: return "multiselect";
                case AttributeEditorKind.Guid: return "guid";
                case AttributeEditorKind.Lookup: return "lookup";
                default: throw new ArgumentOutOfRangeException(nameof(kind), $"{kind} is not an enveloped kind.");
            }
        }

        public static AttributeEditorKind KindFromEnvelopeToken(string token)
        {
            switch (token)
            {
                case "decimal": return AttributeEditorKind.Decimal;
                case "double": return AttributeEditorKind.Double;
                case "money": return AttributeEditorKind.Money;
                case "datetime": return AttributeEditorKind.DateTime;
                case "optionset": return AttributeEditorKind.OptionSet;
                case "multiselect": return AttributeEditorKind.MultiSelectOptionSet;
                case "guid": return AttributeEditorKind.Guid;
                case "lookup": return AttributeEditorKind.Lookup;
                default: throw new FormatException($"Unknown type envelope token '{token}'.");
            }
        }
    }
}
