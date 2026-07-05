using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PluginDebugger.Runtime;

namespace PluginDebugger.Metadata
{
    /// <summary>
    /// Per-connection metadata cache (requirements NFR-4). Fetches entity attribute metadata on
    /// demand and keeps it keyed by logical name so the typed editor stays responsive. A fresh
    /// instance is created whenever the active connection changes.
    /// </summary>
    internal sealed class MetadataCache
    {
        private readonly IOrganizationService _service;
        private readonly Dictionary<string, EntityMetadata> _entities =
            new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);
        private EntityMetadata[] _allEntities;

        public MetadataCache(IOrganizationService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public EntityMetadata GetEntity(string logicalName)
        {
            if (string.IsNullOrWhiteSpace(logicalName))
            {
                throw new ArgumentException("Entity logical name is required.", nameof(logicalName));
            }

            if (_entities.TryGetValue(logicalName, out var cached))
            {
                return cached;
            }

            var response = (RetrieveEntityResponse)_service.Execute(new RetrieveEntityRequest
            {
                LogicalName = logicalName,
                EntityFilters = EntityFilters.Attributes,
                RetrieveAsIfPublished = true
            });

            _entities[logicalName] = response.EntityMetadata;
            return response.EntityMetadata;
        }

        /// <summary>
        /// Attributes the user can meaningfully set on a Target/image: a recognized type, not a
        /// child/virtual sub-attribute, and valid for create or update.
        /// </summary>
        public IReadOnlyList<AttributeMetadata> GetWritableAttributes(string logicalName)
        {
            return GetEntity(logicalName).Attributes
                .Where(a => a.AttributeOf == null)
                .Where(a => AttributeTypeMapper.FromMetadata(a) != null)
                .Where(a => (a.IsValidForCreate ?? false) || (a.IsValidForUpdate ?? false))
                .OrderBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public AttributeMetadata GetAttribute(string logicalName, string attributeName)
        {
            return GetEntity(logicalName).Attributes
                .FirstOrDefault(a => string.Equals(a.LogicalName, attributeName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The list of tables for the table picker (entity-level metadata only, cached).</summary>
        public IReadOnlyList<EntityMetadata> GetAllEntities()
        {
            if (_allEntities != null)
            {
                return _allEntities;
            }

            var response = (RetrieveAllEntitiesResponse)_service.Execute(new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = true
            });

            _allEntities = response.EntityMetadata
                .OrderBy(e => e.LogicalName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return _allEntities;
        }

        public string PrimaryNameAttribute(string logicalName) => GetEntity(logicalName).PrimaryNameAttribute;

        public string PrimaryIdAttribute(string logicalName) => GetEntity(logicalName).PrimaryIdAttribute;
    }
}
