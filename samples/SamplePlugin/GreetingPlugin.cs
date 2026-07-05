using System;
using Microsoft.Xrm.Sdk;

namespace SamplePlugin
{
    /// <summary>
    /// A trivial sample plugin used to validate the debug harness end to end. It exercises
    /// the tracing service, the execution context, the Target, the service factory + a write
    /// (which the harness mocks unless in Full-real mode), and an OutputParameters mutation.
    /// </summary>
    public class GreetingPlugin : IPlugin
    {
        private readonly string _unsecure;

        public GreetingPlugin(string unsecure, string secure)
        {
            _unsecure = unsecure;
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

            trace.Trace("GreetingPlugin started. Message={0}, Stage={1}, Entity={2}",
                context.MessageName, context.Stage, context.PrimaryEntityName);
            trace.Trace("Context: depth={0}, mode={1}, userId={2}", context.Depth, context.Mode, context.UserId);
            if (context.SharedVariables.Contains("note"))
            {
                trace.Trace("SharedVariable note={0}", context.SharedVariables["note"]);
            }
            if (context.InputParameters.Contains("reason"))
            {
                trace.Trace("InputParameter reason={0}", context.InputParameters["reason"]);
            }

            if (context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity target)
            {
                trace.Trace("Target {0} has {1} attribute(s); name='{2}'",
                    target.LogicalName, target.Attributes.Count, target.GetAttributeValue<string>("name"));

                var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                var service = factory.CreateOrganizationService(context.UserId);

                var taskId = service.Create(new Entity("task") { ["subject"] = "Follow up (created by GreetingPlugin)" });
                trace.Trace("Requested creation of task {0}", taskId);

                context.OutputParameters["greeting"] = "Hello, " + target.GetAttributeValue<string>("name");
            }

            trace.Trace("Unsecure config was: '{0}'", _unsecure ?? "(none)");
            trace.Trace("GreetingPlugin finished.");
        }
    }
}
