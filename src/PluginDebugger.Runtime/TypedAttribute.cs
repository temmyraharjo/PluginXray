using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xrm.Sdk;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// One attribute the user has added to a Target or image in the typed editor (requirements §4.5).
    /// Holds the editor kind and a value in a normalized boxed form, and knows how to turn itself
    /// into the correct SDK object for the synthesized context.
    ///
    /// Normalized <see cref="Value"/> forms by kind:
    ///   String/Memo -> string; Boolean -> bool; WholeNumber -> int; BigInt -> long;
    ///   Decimal/Money -> decimal; Double -> double; DateTime -> DateTime; OptionSet -> int;
    ///   MultiSelectOptionSet -> IList&lt;int&gt;; Guid -> Guid; Lookup -> Guid (with <see cref="LookupEntity"/>).
    /// </summary>
    public sealed class TypedAttribute
    {
        public TypedAttribute(string logicalName, AttributeEditorKind kind, object value, string lookupEntity = null)
        {
            LogicalName = logicalName;
            Kind = kind;
            Value = value;
            LookupEntity = lookupEntity;
        }

        public string LogicalName { get; }
        public AttributeEditorKind Kind { get; }
        public object Value { get; }

        /// <summary>For <see cref="AttributeEditorKind.Lookup"/>: the chosen (possibly polymorphic) target entity.</summary>
        public string LookupEntity { get; }

        /// <summary>The SDK object to place in an <c>Entity</c>'s attribute bag.</summary>
        public object ToSdkValue()
        {
            switch (Kind)
            {
                case AttributeEditorKind.String:
                case AttributeEditorKind.Memo:
                    return Value == null ? null : Convert.ToString(Value, CultureInfo.InvariantCulture);
                case AttributeEditorKind.Boolean:
                    return Convert.ToBoolean(Value, CultureInfo.InvariantCulture);
                case AttributeEditorKind.WholeNumber:
                    return Convert.ToInt32(Value, CultureInfo.InvariantCulture);
                case AttributeEditorKind.BigInt:
                    return Convert.ToInt64(Value, CultureInfo.InvariantCulture);
                case AttributeEditorKind.Decimal:
                    return Convert.ToDecimal(Value, CultureInfo.InvariantCulture);
                case AttributeEditorKind.Double:
                    return Convert.ToDouble(Value, CultureInfo.InvariantCulture);
                case AttributeEditorKind.Money:
                    return new Money(Convert.ToDecimal(Value, CultureInfo.InvariantCulture));
                case AttributeEditorKind.DateTime:
                    return Convert.ToDateTime(Value, CultureInfo.InvariantCulture);
                case AttributeEditorKind.OptionSet:
                    return new OptionSetValue(Convert.ToInt32(Value, CultureInfo.InvariantCulture));
                case AttributeEditorKind.MultiSelectOptionSet:
                    return new OptionSetValueCollection(AsIntList(Value).Select(v => new OptionSetValue(v)).ToList());
                case AttributeEditorKind.Guid:
                    return Value is Guid g ? g : Guid.Parse(Convert.ToString(Value, CultureInfo.InvariantCulture));
                case AttributeEditorKind.Lookup:
                    var id = Value is Guid lg ? lg : Guid.Parse(Convert.ToString(Value, CultureInfo.InvariantCulture));
                    return new EntityReference(LookupEntity, id);
                default:
                    throw new InvalidOperationException($"Unsupported attribute kind '{Kind}'.");
            }
        }

        /// <summary>A short one-line description for the editor grid.</summary>
        public string DisplayValue()
        {
            switch (Kind)
            {
                case AttributeEditorKind.MultiSelectOptionSet:
                    return "[" + string.Join(",", AsIntList(Value)) + "]";
                case AttributeEditorKind.Lookup:
                    return $"{LookupEntity}:{Value}";
                case AttributeEditorKind.DateTime:
                    return Value is DateTime dt ? dt.ToString("o", CultureInfo.InvariantCulture) : Convert.ToString(Value);
                default:
                    return Value == null ? "(null)" : Convert.ToString(Value, CultureInfo.InvariantCulture);
            }
        }

        private static IList<int> AsIntList(object value)
        {
            switch (value)
            {
                case null:
                    return new List<int>();
                case IEnumerable<int> ints:
                    return ints.ToList();
                case System.Collections.IEnumerable en:
                    return en.Cast<object>().Select(o => Convert.ToInt32(o, CultureInfo.InvariantCulture)).ToList();
                default:
                    return new List<int> { Convert.ToInt32(value, CultureInfo.InvariantCulture) };
            }
        }

        /// <summary>Builds an SDK <c>Entity</c> from a set of typed attributes.</summary>
        public static Entity ToEntity(string logicalName, IEnumerable<TypedAttribute> attributes)
        {
            var entity = new Entity(logicalName);
            foreach (var attr in attributes)
            {
                entity[attr.LogicalName] = attr.ToSdkValue();
            }
            return entity;
        }
    }
}
