using System;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// A serialized SDK object that can safely cross the AppDomain boundary: it carries only
    /// the concrete type name and the DataContract XML (both strings). SDK types are
    /// <c>[DataContract]</c> but not all are <c>[Serializable]</c>, so they cannot be passed
    /// by-value through a remoting proxy directly — this wrapper is how the service bridge
    /// moves entities, queries, requests and responses between the child and main domains.
    /// </summary>
    [Serializable]
    public sealed class Payload
    {
        public string TypeName { get; set; }
        public string Xml { get; set; }

        /// <summary>Serializes <paramref name="obj"/> using its concrete runtime type.</summary>
        public static Payload For(object obj)
        {
            if (obj == null)
            {
                return new Payload();
            }

            var type = obj.GetType();
            return new Payload
            {
                TypeName = type.AssemblyQualifiedName,
                Xml = SdkXml.Serialize(obj, type)
            };
        }

        /// <summary>Rehydrates the object in the current domain (null if empty).</summary>
        public object ToObject()
        {
            if (string.IsNullOrEmpty(Xml))
            {
                return null;
            }

            var type = SdkXml.ResolveType(TypeName)
                       ?? throw new InvalidOperationException($"Could not resolve type '{TypeName}' for deserialization.");
            return SdkXml.Deserialize(type, Xml);
        }
    }
}
