using System;
using System.Activities;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Workflow;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// Runs INSIDE the child AppDomain. Created via <c>CreateInstanceAndUnwrap</c> from the
    /// main domain, so the main domain holds a proxy and the actual plugin load/execute
    /// happens in the disposable child domain (requirements §4.8).
    ///
    /// The MBRO arguments to <see cref="Execute"/> (service wrapper, tracing sink, log sink)
    /// are proxies back to the main domain; the plugin assembly and the synthesized context
    /// are local to this domain.
    /// </summary>
    public sealed class PluginExecutor : MarshalByRefObject
    {
        private static bool _resolverInstalled;
        private static string _pluginDirectory;
        private static string _hostDirectory;

        public override object InitializeLifetimeService() => null;

        /// <summary>
        /// Installs the child-domain assembly resolver. This MUST be called (a separate cross-domain
        /// call with only string arguments) BEFORE <see cref="Execute"/>, because JIT-compiling
        /// Execute loads the Microsoft.Xrm.Sdk types it references — and that happens before Execute's
        /// first statement runs. If the resolver were installed inside Execute, the SDK load during
        /// JIT would fail in a host (XrmToolBox) whose Plugins folder doesn't contain the SDK.
        /// Its arguments are strings only, so marshaling this call never needs the SDK itself.
        /// </summary>
        public void PrepareResolver(string pluginDirectory, string hostDirectory)
        {
            InstallResolver(pluginDirectory, hostDirectory);
        }

        public RunResult Execute(RunRequest request, ServiceBridge bridge, RunLogSink log)
        {
            var result = new RunResult();
            InstallResolver(request.PluginDirectory, request.HostDirectory);

            try
            {
                log.Log(LogCategory.Info, BuildContextSummary(request));

                var pluginType = LoadPluginType(request.ShadowDllPath, request.PluginTypeName);
                var plugin = Instantiate(pluginType, request.UnsecureConfig, request.SecureConfig, log);

                // Domain-local shims so the plugin's SDK objects never cross the boundary raw.
                var service = new ChildOrganizationService(bridge);
                var tracing = new ChildTracingService(log);

                var context = BuildContext(request.Context);
                var serviceProvider = new PluginServiceProvider(context, tracing, service);

                ((IPlugin)plugin).Execute(serviceProvider);

                CaptureOutputs(context, result, log);
                result.Success = true;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                CaptureException(tie.InnerException, result, log);
            }
            catch (Exception ex)
            {
                CaptureException(ex, result, log);
            }

            return result;
        }

        /// <summary>
        /// Runs a custom workflow activity (code activity) instead of an <see cref="IPlugin"/>
        /// (requirements §4.12). Hosts the activity via <see cref="WorkflowInvoker"/> with the
        /// synthesized <see cref="IWorkflowContext"/>, the service factory (over the bridge), and the
        /// tracing sink as extensions, binds the supplied input arguments, and captures the outputs.
        /// Like <see cref="Execute"/>, this MUST be preceded by <see cref="PrepareResolver"/>.
        /// </summary>
        public RunResult ExecuteWorkflow(RunRequest request, ServiceBridge bridge, RunLogSink log)
        {
            var result = new RunResult();
            InstallResolver(request.PluginDirectory, request.HostDirectory);

            try
            {
                log.Log(LogCategory.Info, BuildContextSummary(request));

                var activityType = LoadPluginType(request.ShadowDllPath, request.PluginTypeName);
                var activity = (Activity)Activator.CreateInstance(activityType);

                var service = new ChildOrganizationService(bridge);
                var factory = new ChildServiceFactory(service);
                var tracing = new ChildTracingService(log);
                var context = BuildWorkflowContext(request.Context);

                var invoker = new WorkflowInvoker(activity);
                invoker.Extensions.Add<IWorkflowContext>(() => context);
                invoker.Extensions.Add<IOrganizationServiceFactory>(() => factory);
                invoker.Extensions.Add<ITracingService>(() => tracing);

                var inputs = BuildWorkflowInputs(request.InputArguments, log);
                var outputs = invoker.Invoke(inputs);

                CaptureWorkflowOutputs(outputs, result, log);
                result.Success = true;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                CaptureException(tie.InnerException, result, log);
            }
            catch (Exception ex)
            {
                CaptureException(ex, result, log);
            }

            return result;
        }

        // ---- assembly resolution -----------------------------------------------------------

        private static void InstallResolver(string pluginDirectory, string hostDirectory)
        {
            _pluginDirectory = pluginDirectory;
            _hostDirectory = hostDirectory;
            if (_resolverInstalled)
            {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            _resolverInstalled = true;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            return AssemblyResolution.Resolve(args.Name, _hostDirectory, _pluginDirectory);
        }

        // ---- load + instantiate ------------------------------------------------------------

        private static Type LoadPluginType(string shadowDllPath, string typeName)
        {
            var assembly = Assembly.LoadFrom(shadowDllPath);
            var type = assembly.GetType(typeName, throwOnError: false)
                       ?? assembly.GetTypes().FirstOrDefault(t => t.FullName == typeName);

            if (type == null)
            {
                throw new InvalidOperationException($"Plugin type '{typeName}' not found in '{Path.GetFileName(shadowDllPath)}'.");
            }

            return type;
        }

        private static object Instantiate(Type type, string unsecure, string secure, ILogSink log)
        {
            // Prefer the platform-standard (unsecure, secure) ctor, then (unsecure), then default.
            var configCtor = type.GetConstructor(new[] { typeof(string), typeof(string) });
            if (configCtor != null)
            {
                log.Log(LogCategory.Info, "Instantiating via (unsecure, secure) constructor.");
                return configCtor.Invoke(new object[] { unsecure, secure });
            }

            var unsecureCtor = type.GetConstructor(new[] { typeof(string) });
            if (unsecureCtor != null)
            {
                log.Log(LogCategory.Info, "Instantiating via (unsecure) constructor.");
                return unsecureCtor.Invoke(new object[] { unsecure });
            }

            var defaultCtor = type.GetConstructor(Type.EmptyTypes);
            if (defaultCtor != null)
            {
                log.Log(LogCategory.Info, "Instantiating via parameterless constructor.");
                return defaultCtor.Invoke(null);
            }

            throw new InvalidOperationException(
                $"Plugin type '{type.FullName}' has no supported public constructor " +
                "(expected (string,string), (string), or parameterless).");
        }

        // ---- context construction ----------------------------------------------------------

        private static SynthesizedPluginExecutionContext BuildContext(ContextDto dto)
        {
            var ctx = new SynthesizedPluginExecutionContext
            {
                MessageName = dto.MessageName,
                PrimaryEntityName = dto.PrimaryEntityName,
                PrimaryEntityId = dto.PrimaryEntityId,
                Stage = dto.Stage,
                Mode = dto.Mode,
                Depth = dto.Depth,
                UserId = dto.UserId,
                InitiatingUserId = dto.InitiatingUserId,
                BusinessUnitId = dto.BusinessUnitId,
                OrganizationId = dto.OrganizationId,
                OrganizationName = dto.OrganizationName,
                CorrelationId = dto.CorrelationId == Guid.Empty ? Guid.NewGuid() : dto.CorrelationId
            };

            // Target
            if (dto.TargetKind == TargetKind.Entity && !string.IsNullOrEmpty(dto.TargetXml))
            {
                ctx.InputParameters["Target"] = SdkXml.Deserialize<Entity>(dto.TargetXml);
            }
            else if (dto.TargetKind == TargetKind.EntityReference && !string.IsNullOrEmpty(dto.TargetXml))
            {
                ctx.InputParameters["Target"] = SdkXml.Deserialize<EntityReference>(dto.TargetXml);
            }

            // Arbitrary InputParameters beyond Target (FR-4.6). The reserved "Target" key stays
            // owned by the form-shape engine above and is never overwritten from here.
            foreach (var param in dto.InputParameters)
            {
                if (string.IsNullOrEmpty(param.Key) ||
                    string.Equals(param.Key, "Target", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ctx.InputParameters[param.Key] = string.IsNullOrEmpty(param.ValueXml)
                    ? null
                    : SdkXml.Deserialize<object>(param.ValueXml);
            }

            // OutputParameters seed (Create / post stage exposes the new id)
            if (dto.OutputId.HasValue)
            {
                ctx.OutputParameters["id"] = dto.OutputId.Value;
            }

            // Images
            foreach (var img in dto.PreImages)
            {
                ctx.PreEntityImages[img.Key] = SdkXml.Deserialize<Entity>(img.EntityXml);
            }
            foreach (var img in dto.PostImages)
            {
                ctx.PostEntityImages[img.Key] = SdkXml.Deserialize<Entity>(img.EntityXml);
            }

            // SharedVariables
            foreach (var sv in dto.SharedVariables)
            {
                ctx.SharedVariables[sv.Key] = string.IsNullOrEmpty(sv.ValueXml)
                    ? null
                    : SdkXml.Deserialize<object>(sv.ValueXml);
            }

            return ctx;
        }

        // ---- result capture ----------------------------------------------------------------

        private static void CaptureOutputs(IPluginExecutionContext ctx, RunResult result, ILogSink log)
        {
            foreach (var kvp in ctx.OutputParameters)
            {
                result.OutputParameters.Add(new OutputParameterDto
                {
                    Key = kvp.Key,
                    Display = SdkXml.Describe(kvp.Value)
                });
            }

            if (ctx.OutputParameters.Count > 0)
            {
                result.OutputParametersXml = SdkXml.Serialize(ctx.OutputParameters, typeof(ParameterCollection));
            }

            log.Log(LogCategory.Info, $"Plugin completed. OutputParameters: {ctx.OutputParameters.Count}.");
        }

        // ---- workflow-activity construction (§4.12) ----------------------------------------

        private static SynthesizedWorkflowContext BuildWorkflowContext(ContextDto dto)
        {
            var ctx = new SynthesizedWorkflowContext
            {
                MessageName = dto.MessageName,
                PrimaryEntityName = dto.PrimaryEntityName,
                PrimaryEntityId = dto.PrimaryEntityId,
                Mode = dto.Mode,
                Depth = dto.Depth,
                UserId = dto.UserId,
                InitiatingUserId = dto.InitiatingUserId,
                BusinessUnitId = dto.BusinessUnitId,
                OrganizationId = dto.OrganizationId,
                OrganizationName = dto.OrganizationName,
                CorrelationId = dto.CorrelationId == Guid.Empty ? Guid.NewGuid() : dto.CorrelationId,
                StageName = dto.StageName,
                WorkflowCategory = dto.WorkflowCategory,
                WorkflowMode = dto.WorkflowMode
            };

            foreach (var sv in dto.SharedVariables)
            {
                ctx.SharedVariables[sv.Key] = string.IsNullOrEmpty(sv.ValueXml)
                    ? null
                    : SdkXml.Deserialize<object>(sv.ValueXml);
            }

            return ctx;
        }

        private static Dictionary<string, object> BuildWorkflowInputs(
            List<WorkflowArgumentDto> arguments, ILogSink log)
        {
            var inputs = new Dictionary<string, object>();
            foreach (var arg in arguments)
            {
                if (string.IsNullOrEmpty(arg.Name))
                {
                    continue;
                }

                var value = string.IsNullOrEmpty(arg.ValueXml) ? null : SdkXml.Deserialize<object>(arg.ValueXml);
                inputs[arg.Name] = value;
                log.Log(LogCategory.Info, $"InArgument[{arg.Name}] = {SdkXml.Describe(value)}");
            }
            return inputs;
        }

        private static void CaptureWorkflowOutputs(IDictionary<string, object> outputs, RunResult result, ILogSink log)
        {
            if (outputs != null)
            {
                foreach (var kvp in outputs)
                {
                    result.OutputParameters.Add(new OutputParameterDto
                    {
                        Key = kvp.Key,
                        Display = SdkXml.Describe(kvp.Value)
                    });
                }
            }

            log.Log(LogCategory.Info, $"Activity completed. Output arguments: {result.OutputParameters.Count}.");
        }

        private static void CaptureException(Exception ex, RunResult result, ILogSink log)
        {
            result.Success = false;
            result.ExceptionType = ex.GetType().FullName;
            result.ExceptionMessage = ex.Message;
            result.ExceptionStackTrace = ex.StackTrace;
            result.ThrewInvalidPluginExecutionException =
                ex.GetType().FullName == "Microsoft.Xrm.Sdk.InvalidPluginExecutionException";

            log.Log(LogCategory.Error,
                $"{(result.ThrewInvalidPluginExecutionException ? "InvalidPluginExecutionException" : ex.GetType().Name)}: {ex.Message}");
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                log.Log(LogCategory.Error, ex.StackTrace);
            }
        }

        private static string BuildContextSummary(RunRequest req)
        {
            var c = req.Context;
            return $"Run: {c.MessageName} / stage {c.Stage} / {c.PrimaryEntityName} " +
                   $"| mode={req.Mode} | depth={c.Depth} " +
                   $"| target={c.TargetKind} | inputParams={c.InputParameters.Count} " +
                   $"| preImages={c.PreImages.Count} postImages={c.PostImages.Count} " +
                   $"| type={req.PluginTypeName}";
        }
    }
}
