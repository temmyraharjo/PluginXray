using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// The keystone (requirements §4.7 + §4.8.6). A single component that is BOTH:
    ///   (a) the bridge that marshals SDK calls from the child AppDomain back to the real
    ///       connection that lives in the MAIN domain, and
    ///   (b) the enforcer of the execution-mode policy (real / read-real-write-mock / full-mock).
    ///
    /// It derives from <see cref="MarshalByRefObject"/> and is constructed in the MAIN domain
    /// where the authenticated <see cref="IOrganizationService"/> already exists. The child
    /// domain holds a transparent proxy and talks to it exclusively through <see cref="Payload"/>
    /// (string) values, because raw SDK objects are not all <c>[Serializable]</c> and cannot be
    /// passed through a remoting proxy by value. The real ServiceClient never crosses the
    /// boundary and is never re-authenticated there.
    /// </summary>
    public sealed class ServiceBridge : MarshalByRefObject
    {
        private readonly IOrganizationService _real;
        private readonly ExecutionMode _mode;
        private readonly ILogSink _log;

        // Request names that are reads — safe to send to the live environment in any mode
        // except FullMock. Compared case-insensitively against OrganizationRequest.RequestName.
        private static readonly HashSet<string> ReadRequestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Retrieve", "RetrieveMultiple", "WhoAmI",
            "RetrieveEntity", "RetrieveAllEntities", "RetrieveAttribute",
            "RetrieveRelationship", "RetrieveOptionSet", "RetrieveAllOptionSets",
            "RetrieveEntityChanges", "RetrieveMetadataChanges",
            "RetrievePrincipalAccess", "QueryExpressionToFetchXml", "FetchXmlToQueryExpression",
            "RetrieveCurrentOrganization", "RetrieveVersion", "ExecuteFetch"
        };

        public ServiceBridge(IOrganizationService real, ExecutionMode mode, ILogSink log)
        {
            _real = real ?? throw new ArgumentNullException(nameof(real));
            _mode = mode;
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public override object InitializeLifetimeService() => null;

        // ---- writes ------------------------------------------------------------------------

        public Guid Create(Payload entityPayload)
        {
            var entity = (Entity)entityPayload.ToObject();
            if (_mode == ExecutionMode.FullReal)
            {
                _log.Log(LogCategory.SdkReal, $"Create {entity.LogicalName} ({entity.Attributes.Count} attrs)");
                return _real.Create(entity);
            }

            var mockId = Guid.NewGuid();
            _log.Log(LogCategory.SdkMock, $"Create {entity.LogicalName} ({entity.Attributes.Count} attrs) -> NOT executed; returning mock id {mockId}");
            return mockId;
        }

        public void Update(Payload entityPayload)
        {
            var entity = (Entity)entityPayload.ToObject();
            if (_mode == ExecutionMode.FullReal)
            {
                _log.Log(LogCategory.SdkReal, $"Update {entity.LogicalName} {entity.Id} ({entity.Attributes.Count} attrs)");
                _real.Update(entity);
                return;
            }

            _log.Log(LogCategory.SdkMock, $"Update {entity.LogicalName} {entity.Id} ({entity.Attributes.Count} attrs) -> NOT executed");
        }

        public void Delete(string entityName, Guid id)
        {
            if (_mode == ExecutionMode.FullReal)
            {
                _log.Log(LogCategory.SdkReal, $"Delete {entityName} {id}");
                _real.Delete(entityName, id);
                return;
            }

            _log.Log(LogCategory.SdkMock, $"Delete {entityName} {id} -> NOT executed");
        }

        public void Associate(string entityName, Guid entityId, Payload relationshipPayload, Payload relatedPayload)
        {
            var relationship = (Relationship)relationshipPayload.ToObject();
            var related = (EntityReferenceCollection)relatedPayload.ToObject();
            if (_mode == ExecutionMode.FullReal)
            {
                _log.Log(LogCategory.SdkReal, $"Associate {entityName} {entityId} via {relationship?.SchemaName}");
                _real.Associate(entityName, entityId, relationship, related);
                return;
            }

            _log.Log(LogCategory.SdkMock, $"Associate {entityName} {entityId} via {relationship?.SchemaName} -> NOT executed");
        }

        public void Disassociate(string entityName, Guid entityId, Payload relationshipPayload, Payload relatedPayload)
        {
            var relationship = (Relationship)relationshipPayload.ToObject();
            var related = (EntityReferenceCollection)relatedPayload.ToObject();
            if (_mode == ExecutionMode.FullReal)
            {
                _log.Log(LogCategory.SdkReal, $"Disassociate {entityName} {entityId} via {relationship?.SchemaName}");
                _real.Disassociate(entityName, entityId, relationship, related);
                return;
            }

            _log.Log(LogCategory.SdkMock, $"Disassociate {entityName} {entityId} via {relationship?.SchemaName} -> NOT executed");
        }

        // ---- reads -------------------------------------------------------------------------

        public Payload Retrieve(string entityName, Guid id, Payload columnSetPayload)
        {
            if (_mode == ExecutionMode.FullMock)
            {
                _log.Log(LogCategory.SdkMock, $"Retrieve {entityName} {id} -> mocked (empty entity returned)");
                return Payload.For(new Entity(entityName, id));
            }

            var columnSet = (ColumnSet)columnSetPayload.ToObject();
            _log.Log(LogCategory.SdkReal, $"Retrieve {entityName} {id}");
            return Payload.For(_real.Retrieve(entityName, id, columnSet));
        }

        public Payload RetrieveMultiple(Payload queryPayload)
        {
            var query = (QueryBase)queryPayload.ToObject();
            if (_mode == ExecutionMode.FullMock)
            {
                _log.Log(LogCategory.SdkMock, $"RetrieveMultiple {DescribeQuery(query)} -> mocked (empty collection returned)");
                return Payload.For(new EntityCollection { EntityName = (query as QueryExpression)?.EntityName });
            }

            _log.Log(LogCategory.SdkReal, $"RetrieveMultiple {DescribeQuery(query)}");
            return Payload.For(_real.RetrieveMultiple(query));
        }

        // ---- Execute (the catch-all) -------------------------------------------------------

        public Payload Execute(Payload requestPayload)
        {
            var request = (OrganizationRequest)requestPayload.ToObject();
            var name = request?.RequestName ?? request?.GetType().Name ?? "(unknown)";
            bool isRead = request != null && ReadRequestNames.Contains(request.RequestName ?? string.Empty);

            if (_mode == ExecutionMode.FullReal)
            {
                _log.Log(LogCategory.SdkReal, $"Execute {name}");
                return Payload.For(_real.Execute(request));
            }

            if (_mode == ExecutionMode.ReadRealWriteMock && isRead)
            {
                _log.Log(LogCategory.SdkReal, $"Execute {name} (read)");
                return Payload.For(_real.Execute(request));
            }

            _log.Log(LogCategory.SdkMock, $"Execute {name} -> NOT executed; returning empty response");
            return Payload.For(BuildMockResponse(request));
        }

        // ---- helpers -----------------------------------------------------------------------

        private static OrganizationResponse BuildMockResponse(OrganizationRequest request)
        {
            var response = new OrganizationResponse { ResponseName = request?.RequestName };
            if (request == null)
            {
                return response;
            }

            if (string.Equals(request.RequestName, "Create", StringComparison.OrdinalIgnoreCase))
            {
                response.Results["id"] = Guid.NewGuid();
            }
            else if (string.Equals(request.RequestName, "Retrieve", StringComparison.OrdinalIgnoreCase))
            {
                var target = request.Parameters.Contains("Target") ? request.Parameters["Target"] as EntityReference : null;
                response.Results["Entity"] = target != null ? new Entity(target.LogicalName, target.Id) : new Entity();
            }
            else if (string.Equals(request.RequestName, "RetrieveMultiple", StringComparison.OrdinalIgnoreCase))
            {
                response.Results["EntityCollection"] = new EntityCollection();
            }

            return response;
        }

        private static string DescribeQuery(QueryBase query)
        {
            switch (query)
            {
                case QueryExpression qe:
                    return qe.EntityName;
                case QueryByAttribute qba:
                    return qba.EntityName;
                case FetchExpression fe:
                    var q = fe.Query ?? string.Empty;
                    return "fetchxml(" + new string(q.Take(60).ToArray()) + (q.Length > 60 ? "…" : "") + ")";
                default:
                    return query?.GetType().Name ?? "(null)";
            }
        }
    }
}
