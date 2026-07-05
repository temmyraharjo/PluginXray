using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// Enumerates the IPlugin types AND custom workflow activity (CodeActivity) types in an assembly
    /// from inside a throwaway child AppDomain, so browsing an assembly never locks the original dll
    /// on disk (requirements §4.6, §4.8, §4.12).
    /// </summary>
    public sealed class AssemblyInspector : MarshalByRefObject
    {
        private string _directory;
        private string _hostDirectory;

        public override object InitializeLifetimeService() => null;

        public PluginTypeInfo[] GetPluginTypes(string dllPath, string hostDirectory)
        {
            _directory = Path.GetDirectoryName(dllPath);
            _hostDirectory = hostDirectory;
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            var assembly = Assembly.LoadFrom(dllPath);

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Still surface the types that did load.
                types = ex.Types.Where(t => t != null).ToArray();
            }

            var results = new List<PluginTypeInfo>();
            foreach (var type in types)
            {
                if (type == null || type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                if (ImplementsIPlugin(type))
                {
                    results.Add(new PluginTypeInfo
                    {
                        FullName = type.FullName,
                        Kind = PluginTypeKind.Plugin,
                        HasConfigCtor = type.GetConstructor(new[] { typeof(string), typeof(string) }) != null,
                        HasUnsecureCtor = type.GetConstructor(new[] { typeof(string) }) != null,
                        HasDefaultCtor = type.GetConstructor(Type.EmptyTypes) != null
                    });
                }
                else if (IsCodeActivity(type))
                {
                    results.Add(new PluginTypeInfo
                    {
                        FullName = type.FullName,
                        Kind = PluginTypeKind.WorkflowActivity,
                        HasDefaultCtor = type.GetConstructor(Type.EmptyTypes) != null,
                        Arguments = ReflectArguments(type)
                    });
                }
            }

            return results
                .OrderBy(r => r.Kind)
                .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool ImplementsIPlugin(Type type)
        {
            // Compare by full name so we don't depend on the plugin referencing the exact
            // same Microsoft.Xrm.Sdk version this domain loaded.
            return type.GetInterfaces().Any(i => i.FullName == "Microsoft.Xrm.Sdk.IPlugin");
        }

        /// <summary>True if the type derives (transitively) from System.Activities.CodeActivity (§4.12).</summary>
        private static bool IsCodeActivity(Type type)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                if (t.FullName == "System.Activities.CodeActivity")
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Reflects the activity's In/Out/InOut argument properties (InArgument&lt;T&gt; etc.),
        /// capturing name, direction, value type, required-ness and friendly label (FR-12.4/12.5).
        /// </summary>
        private static List<WorkflowArgumentInfo> ReflectArguments(Type type)
        {
            var arguments = new List<WorkflowArgumentInfo>();

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propertyType = property.PropertyType;
                if (!propertyType.IsGenericType)
                {
                    continue;
                }

                var direction = DirectionOf(propertyType.GetGenericTypeDefinition().FullName);
                if (direction == null)
                {
                    continue;
                }

                arguments.Add(new WorkflowArgumentInfo
                {
                    Name = property.Name,
                    DisplayName = FriendlyName(property) ?? property.Name,
                    Direction = direction,
                    TypeName = propertyType.GetGenericArguments()[0].Name,
                    Required = HasAttribute(property, "System.Activities.RequiredArgumentAttribute")
                });
            }

            return arguments.OrderBy(a => a.Direction).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string DirectionOf(string genericDefinitionFullName)
        {
            if (genericDefinitionFullName == null)
            {
                return null;
            }
            if (genericDefinitionFullName.StartsWith("System.Activities.InArgument", StringComparison.Ordinal)) return "In";
            if (genericDefinitionFullName.StartsWith("System.Activities.OutArgument", StringComparison.Ordinal)) return "Out";
            if (genericDefinitionFullName.StartsWith("System.Activities.InOutArgument", StringComparison.Ordinal)) return "InOut";
            return null;
        }

        /// <summary>The [Input]/[Output] display name if present (matched by attribute type name).</summary>
        private static string FriendlyName(PropertyInfo property)
        {
            foreach (var attribute in property.GetCustomAttributes(false))
            {
                var name = attribute.GetType().FullName;
                if (name == "Microsoft.Xrm.Sdk.Workflow.InputAttribute" ||
                    name == "Microsoft.Xrm.Sdk.Workflow.OutputAttribute")
                {
                    // Both expose a single string via their Name/DisplayName property or ctor arg.
                    var textProperty = attribute.GetType().GetProperty("Name") ?? attribute.GetType().GetProperty("DisplayName");
                    var value = textProperty?.GetValue(attribute, null) as string;
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
            return null;
        }

        private static bool HasAttribute(PropertyInfo property, string attributeFullName)
        {
            return property.GetCustomAttributes(false).Any(a => a.GetType().FullName == attributeFullName);
        }

        private Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            return AssemblyResolution.Resolve(args.Name, _hostDirectory, _directory);
        }
    }
}
