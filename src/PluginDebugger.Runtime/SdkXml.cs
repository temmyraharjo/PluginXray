using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// Serializes SDK objects (Entity / EntityReference / images / parameter collections)
    /// to and from XML using <see cref="DataContractSerializer"/>.
    ///
    /// Why not just put SDK objects in a [Serializable] DTO and let AppDomain remoting
    /// move them? Because the Xrm.Sdk types are <c>[DataContract]</c> but not all are
    /// <c>[Serializable]</c>, so BinaryFormatter-based remoting is unreliable for them.
    /// Serializing to an XML string in the main domain and rehydrating in the child domain
    /// sidesteps that entirely — the DTOs that cross the boundary then contain only
    /// primitives and strings.
    /// </summary>
    public static class SdkXml
    {
        // Polymorphic attribute values are typed as object; the serializer needs to be
        // told the concrete types it may encounter.
        private static readonly Type[] KnownTypes =
        {
            typeof(Entity),
            typeof(EntityReference),
            typeof(EntityCollection),
            typeof(EntityReferenceCollection),
            typeof(OptionSetValue),
            typeof(OptionSetValueCollection),
            typeof(Money),
            typeof(AliasedValue),
            typeof(BooleanManagedProperty),
            typeof(EntityImageCollection),
            typeof(ParameterCollection),
            typeof(AttributeCollection),
            typeof(FormattedValueCollection),
            typeof(byte[]),
            typeof(Guid),
            typeof(Guid[]),
            typeof(string[]),
            // Boxed primitives carried via SharedVariables (serialized as object).
            typeof(int),
            typeof(long),
            typeof(bool),
            typeof(decimal),
            typeof(double),
            typeof(DateTime),
            // Query + request/response shapes that cross the service bridge.
            typeof(ColumnSet),
            typeof(QueryExpression),
            typeof(QueryByAttribute),
            typeof(FetchExpression),
            typeof(ConditionExpression),
            typeof(FilterExpression),
            typeof(OrganizationRequest),
            typeof(OrganizationResponse),
            typeof(Relationship)
        };

        public static string Serialize(object obj, Type type)
        {
            if (obj == null)
            {
                return null;
            }

            var serializer = new DataContractSerializer(type, KnownTypes);
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings { OmitXmlDeclaration = true };
            using (var writer = XmlWriter.Create(sb, settings))
            {
                serializer.WriteObject(writer, obj);
            }

            return sb.ToString();
        }

        public static T Deserialize<T>(string xml)
        {
            return (T)Deserialize(typeof(T), xml);
        }

        public static object Deserialize(Type type, string xml)
        {
            if (string.IsNullOrEmpty(xml))
            {
                return null;
            }

            var serializer = new DataContractSerializer(type, KnownTypes);
            using (var reader = XmlReader.Create(new StringReader(xml)))
            {
                return serializer.ReadObject(reader);
            }
        }

        /// <summary>
        /// Resolves a type from its assembly-qualified name, falling back to a simple-name match
        /// across already-loaded assemblies (the SDK assemblies are unified across both domains,
        /// so the version embedded in the AQN may differ slightly).
        /// </summary>
        public static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            var type = Type.GetType(typeName, throwOnError: false);
            if (type != null)
            {
                return type;
            }

            var simpleName = typeName.Split(',')[0].Trim();
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(simpleName, throwOnError: false))
                .FirstOrDefault(t => t != null);
        }

        /// <summary>
        /// Produces a short, human-readable one-line description of an SDK attribute value
        /// for the run log (e.g. <c>OptionSetValue(2)</c>, <c>EntityReference(account, &lt;guid&gt;)</c>).
        /// </summary>
        public static string Describe(object value)
        {
            switch (value)
            {
                case null:
                    return "null";
                case EntityReference er:
                    return $"EntityReference({er.LogicalName}, {er.Id})";
                case OptionSetValue osv:
                    return $"OptionSetValue({osv.Value})";
                case Money money:
                    return $"Money({money.Value})";
                case OptionSetValueCollection col:
                    return "MultiSelect([" + string.Join(",", ValuesOf(col)) + "])";
                case Entity e:
                    return $"Entity({e.LogicalName}, {e.Attributes.Count} attrs)";
                case bool b:
                    return b ? "true" : "false";
                default:
                    return value.ToString();
            }
        }

        private static IEnumerable<int> ValuesOf(OptionSetValueCollection col)
        {
            foreach (var v in col)
            {
                yield return v.Value;
            }
        }
    }
}
