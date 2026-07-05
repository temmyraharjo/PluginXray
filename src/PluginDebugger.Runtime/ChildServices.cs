using System;
using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// The <see cref="IOrganizationService"/> handed to the plugin INSIDE the child domain.
    /// It is a plain, domain-local object, so the plugin's calls (which pass non-serializable
    /// SDK objects) never touch the AppDomain boundary directly. Each call serializes its
    /// arguments to a <see cref="Payload"/> and forwards to the main-domain
    /// <see cref="ServiceBridge"/> proxy, then rehydrates the response locally.
    /// </summary>
    public sealed class ChildOrganizationService : IOrganizationService
    {
        private readonly ServiceBridge _bridge;

        public ChildOrganizationService(ServiceBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        public Guid Create(Entity entity) => _bridge.Create(Payload.For(entity));

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) =>
            (Entity)_bridge.Retrieve(entityName, id, Payload.For(columnSet)).ToObject();

        public void Update(Entity entity) => _bridge.Update(Payload.For(entity));

        public void Delete(string entityName, Guid id) => _bridge.Delete(entityName, id);

        public EntityCollection RetrieveMultiple(QueryBase query) =>
            (EntityCollection)_bridge.RetrieveMultiple(Payload.For(query)).ToObject();

        public OrganizationResponse Execute(OrganizationRequest request) =>
            (OrganizationResponse)_bridge.Execute(Payload.For(request)).ToObject();

        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            _bridge.Associate(entityName, entityId, Payload.For(relationship), Payload.For(relatedEntities));

        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            _bridge.Disassociate(entityName, entityId, Payload.For(relationship), Payload.For(relatedEntities));
    }

    /// <summary>
    /// An <see cref="IOrganizationServiceFactory"/> that always yields the given domain-local
    /// service (the <see cref="ChildOrganizationService"/> over the bridge). Impersonation as an
    /// arbitrary user is out of scope (§6). Used as a WorkflowInvoker extension for code activities
    /// (§4.12) and mirrors the factory inside <see cref="PluginServiceProvider"/>.
    /// </summary>
    public sealed class ChildServiceFactory : IOrganizationServiceFactory
    {
        private readonly IOrganizationService _service;

        public ChildServiceFactory(IOrganizationService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public IOrganizationService CreateOrganizationService(Guid? userId) => _service;
    }

    /// <summary>
    /// The <see cref="ITracingService"/> handed to the plugin INSIDE the child domain. It
    /// formats the message locally (so a plugin tracing a non-serializable object as a format
    /// argument doesn't break marshaling) and forwards only the finished string to the
    /// main-domain log sink.
    /// </summary>
    public sealed class ChildTracingService : ITracingService
    {
        private readonly ILogSink _log;

        public ChildTracingService(ILogSink log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public void Trace(string format, params object[] args)
        {
            string message;
            if (string.IsNullOrEmpty(format))
            {
                message = string.Empty;
            }
            else if (args == null || args.Length == 0)
            {
                message = format;
            }
            else
            {
                try
                {
                    message = string.Format(CultureInfo.InvariantCulture, format, args);
                }
                catch (FormatException)
                {
                    message = format;
                }
            }

            _log.Log(LogCategory.Trace, message);
        }
    }
}
