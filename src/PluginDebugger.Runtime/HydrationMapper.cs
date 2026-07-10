using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// Converts a retrieved <see cref="Entity"/> into typed editor attributes (requirements §4.2).
    /// This is the reverse of <see cref="TypedAttribute.ToSdkValue"/> and is metadata-independent:
    /// the SDK already boxes each value as its concrete type (OptionSetValue, Money, EntityReference,
    /// …), so the kind is inferred from the runtime value.
    /// </summary>
    public static class HydrationMapper
    {
        public static List<TypedAttribute> FromEntity(Entity entity)
        {
            var result = new List<TypedAttribute>();
            if (entity == null)
            {
                return result;
            }

            foreach (var attribute in entity.Attributes)
            {
                var typed = FromValue(attribute.Key, attribute.Value);
                if (typed != null)
                {
                    result.Add(typed);
                }
            }

            return result;
        }

        /// <summary>
        /// Copies an entity's <c>FormattedValues</c> into a plain string map for the FormattedValues
        /// editor (requirements FR-5.7). Empty when the entity is null or carries none.
        /// </summary>
        public static Dictionary<string, string> FormattedValuesFrom(Entity entity)
        {
            var result = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            if (entity?.FormattedValues != null)
            {
                foreach (var pair in entity.FormattedValues)
                {
                    result[pair.Key] = pair.Value;
                }
            }
            return result;
        }

        /// <summary>Maps one boxed SDK value to a <see cref="TypedAttribute"/>, or null if unsupported.</summary>
        public static TypedAttribute FromValue(string name, object value)
        {
            switch (value)
            {
                case null:
                    return null;
                case string s:
                    return new TypedAttribute(name, AttributeEditorKind.String, s);
                case bool b:
                    return new TypedAttribute(name, AttributeEditorKind.Boolean, b);
                case int i:
                    return new TypedAttribute(name, AttributeEditorKind.WholeNumber, i);
                case long l:
                    return new TypedAttribute(name, AttributeEditorKind.BigInt, l);
                case decimal d:
                    return new TypedAttribute(name, AttributeEditorKind.Decimal, d);
                case double db:
                    return new TypedAttribute(name, AttributeEditorKind.Double, db);
                case Money m:
                    return new TypedAttribute(name, AttributeEditorKind.Money, m.Value);
                case System.DateTime dt:
                    return new TypedAttribute(name, AttributeEditorKind.DateTime, dt);
                case OptionSetValue osv:
                    return new TypedAttribute(name, AttributeEditorKind.OptionSet, osv.Value);
                case OptionSetValueCollection col:
                    return new TypedAttribute(name, AttributeEditorKind.MultiSelectOptionSet, col.Select(o => o.Value).ToList());
                case EntityReference er:
                    return new TypedAttribute(name, AttributeEditorKind.Lookup, er.Id, er.LogicalName);
                case System.Guid g:
                    return new TypedAttribute(name, AttributeEditorKind.Guid, g);
                case AliasedValue av:
                    return FromValue(name, av.Value);
                default:
                    // byte[], EntityCollection, BooleanManagedProperty, etc. are not editable here.
                    return null;
            }
        }
    }
}
