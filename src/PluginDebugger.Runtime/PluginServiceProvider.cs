using System;
using Microsoft.Xrm.Sdk;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// The <see cref="IServiceProvider"/> handed to <c>IPlugin.Execute</c> inside the child
    /// domain. Hands back the synthesized context, the tracing sink, and an
    /// <see cref="IOrganizationServiceFactory"/> that yields the marshaled
    /// <see cref="ServiceWrapper"/> proxy.
    /// </summary>
    public sealed class PluginServiceProvider : IServiceProvider
    {
        private readonly IPluginExecutionContext _context;
        private readonly ITracingService _tracing;
        private readonly IOrganizationServiceFactory _factory;

        public PluginServiceProvider(IPluginExecutionContext context, ITracingService tracing, IOrganizationService service)
        {
            _context = context;
            _tracing = tracing;
            _factory = new SingleServiceFactory(service);
        }

        public object GetService(Type serviceType)
        {
            if (serviceType == typeof(IPluginExecutionContext) || serviceType == typeof(IExecutionContext))
            {
                return _context;
            }

            if (serviceType == typeof(ITracingService))
            {
                return _tracing;
            }

            if (serviceType == typeof(IOrganizationServiceFactory))
            {
                return _factory;
            }

            // IServiceEndpointNotificationService and anything else: not supported in the harness.
            return null;
        }

        /// <summary>
        /// Returns the same wrapper regardless of the requested user id. Impersonation as an
        /// arbitrary user is out of scope (§6); the wrapper always runs as the connection user.
        /// </summary>
        private sealed class SingleServiceFactory : IOrganizationServiceFactory
        {
            private readonly IOrganizationService _service;

            public SingleServiceFactory(IOrganizationService service)
            {
                _service = service;
            }

            public IOrganizationService CreateOrganizationService(Guid? userId) => _service;
        }
    }
}
