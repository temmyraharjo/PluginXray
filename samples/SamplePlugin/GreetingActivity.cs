using System.Activities;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Workflow;

namespace SamplePlugin
{
    /// <summary>
    /// A trivial custom workflow activity (code activity) used to validate the harness's §4.12
    /// support. It reads an input argument, traces the workflow context, optionally issues a write
    /// (mocked unless Full-real), and returns an output argument.
    /// </summary>
    public sealed class GreetingActivity : CodeActivity
    {
        [RequiredArgument]
        [Input("Person name")]
        public InArgument<string> Name { get; set; }

        [Input("Times")]
        public InArgument<int> Times { get; set; }

        [Output("Greeting")]
        public OutArgument<string> Greeting { get; set; }

        protected override void Execute(CodeActivityContext executionContext)
        {
            var trace = executionContext.GetExtension<ITracingService>();
            var context = executionContext.GetExtension<IWorkflowContext>();

            var name = Name.Get(executionContext);
            var times = Times.Get(executionContext);

            trace.Trace("GreetingActivity started. Message={0}, StageName={1}, WorkflowMode={2}",
                context.MessageName, context.StageName, context.WorkflowMode);
            trace.Trace("Name='{0}', Times={1}", name, times);

            var factory = executionContext.GetExtension<IOrganizationServiceFactory>();
            var service = factory.CreateOrganizationService(context.UserId);
            var taskId = service.Create(new Entity("task") { ["subject"] = "Follow up (created by GreetingActivity)" });
            trace.Trace("Requested creation of task {0}", taskId);

            var greeting = "Hello, " + name + (times > 1 ? " x" + times : string.Empty);
            Greeting.Set(executionContext, greeting);
            trace.Trace("GreetingActivity finished.");
        }
    }
}
