using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Xrm.Sdk;
using PluginDebugger.Dialogs;
using PluginDebugger.Metadata;
using PluginDebugger.Runtime;

namespace PluginDebugger.Controls
{
    /// <summary>
    /// Add-only typed attribute editor (requirements §4.5). The user adds just the attributes
    /// they care about; each gets a metadata-driven, type-appropriate input via
    /// <see cref="AddAttributeDialog"/>. Supports JSON import/export with the typed envelope
    /// (FR-5.5). Used for both the Target and image editors (FR-5.4).
    /// </summary>
    internal sealed class TypedAttributeEditor : UserControl
    {
        private readonly DataGridView _grid;
        private readonly List<TypedAttribute> _attributes = new List<TypedAttribute>();

        private IOrganizationService _service;
        private MetadataCache _metadata;
        private string _entityName;

        public TypedAttributeEditor()
        {
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, FlowDirection = FlowDirection.LeftToRight };
            AddButton(toolbar, "Add…", (s, e) => AddAttribute());
            AddButton(toolbar, "Edit…", (s, e) => EditSelected());
            AddButton(toolbar, "Remove", (s, e) => RemoveSelected());
            AddButton(toolbar, "Edit as JSON…", (s, e) => EditJson());

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false
            };
            _grid.Columns.Add("attr", "Attribute");
            _grid.Columns.Add("type", "Type");
            _grid.Columns.Add("val", "Value");
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) EditSelected(); };

            Controls.Add(_grid);
            Controls.Add(toolbar);
        }

        /// <summary>The attributes currently in the editor.</summary>
        public IReadOnlyList<TypedAttribute> Attributes => _attributes;

        /// <summary>
        /// Optional hook the host sets (to <c>PluginControlBase.ExecuteMethod</c>) so that adding
        /// or editing an attribute while disconnected opens XrmToolBox's connection selection
        /// dialog and replays the action once connected, instead of erroring.
        /// </summary>
        public Action<Action> RunWithConnection { get; set; }

        public void SetContext(IOrganizationService service, MetadataCache metadata, string entityName)
        {
            _service = service;
            _metadata = metadata;
            _entityName = entityName;
        }

        public void Clear()
        {
            _attributes.Clear();
            RefreshGrid();
        }

        public void SetAttributes(IEnumerable<TypedAttribute> attributes)
        {
            _attributes.Clear();
            _attributes.AddRange(attributes);
            RefreshGrid();
        }

        // ---- actions -----------------------------------------------------------------------

        private void AddAttribute()
        {
            if (RequestConnectionIfNeeded(AddAttribute) || !EnsureReady())
            {
                return;
            }

            using (var dialog = new AddAttributeDialog(_service, _metadata, _entityName))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result != null)
                {
                    Upsert(dialog.Result);
                }
            }
        }

        private void EditSelected()
        {
            if (RequestConnectionIfNeeded(EditSelected) || !EnsureReady())
            {
                return;
            }

            var existing = SelectedAttribute();
            if (existing == null)
            {
                return;
            }

            using (var dialog = new AddAttributeDialog(_service, _metadata, _entityName, existing))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result != null)
                {
                    Upsert(dialog.Result);
                }
            }
        }

        private void RemoveSelected()
        {
            var existing = SelectedAttribute();
            if (existing != null)
            {
                _attributes.Remove(existing);
                RefreshGrid();
            }
        }

        private void EditJson()
        {
            // Pre-fill with the current attributes so the JSON view is an add/edit surface (FR-5.6).
            var initialJson = AttributeJson.Export(_attributes);
            using (var dialog = new EditJsonDialog(initialJson, ResolveKind))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                if (dialog.Mode == EditJsonDialog.ApplyMode.Replace)
                {
                    SetAttributes(dialog.ResultAttributes);
                }
                else
                {
                    foreach (var attr in dialog.ResultAttributes)
                    {
                        Upsert(attr);
                    }
                }
            }
        }

        private AttributeEditorKind? ResolveKind(string attributeName)
        {
            if (_metadata == null || string.IsNullOrEmpty(_entityName))
            {
                return null;
            }

            try
            {
                return AttributeTypeMapper.FromMetadata(_metadata.GetAttribute(_entityName, attributeName));
            }
            catch
            {
                return null;
            }
        }

        // ---- helpers -----------------------------------------------------------------------

        /// <summary>
        /// When disconnected, hands <paramref name="retry"/> to the host's connection flow (which
        /// opens the connection dialog and replays it once connected) and returns true so the
        /// caller stops. Returns false when a connection is already present.
        /// </summary>
        private bool RequestConnectionIfNeeded(Action retry)
        {
            if (_service == null && RunWithConnection != null)
            {
                RunWithConnection(retry);
                return true;
            }
            return false;
        }

        private bool EnsureReady()
        {
            if (_service == null || _metadata == null || string.IsNullOrWhiteSpace(_entityName))
            {
                MessageBox.Show(this,
                    "Select a table (primary entity) first — attributes are metadata-driven.",
                    "Typed editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            try
            {
                _metadata.GetEntity(_entityName);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not load metadata for '" + _entityName + "': " + ex.Message,
                    "Typed editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void Upsert(TypedAttribute attr)
        {
            _attributes.RemoveAll(a => string.Equals(a.LogicalName, attr.LogicalName, StringComparison.OrdinalIgnoreCase));
            _attributes.Add(attr);
            RefreshGrid();
        }

        private TypedAttribute SelectedAttribute()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                return null;
            }

            var name = _grid.SelectedRows[0].Cells[0].Value?.ToString();
            return _attributes.FirstOrDefault(a => a.LogicalName == name);
        }

        private void RefreshGrid()
        {
            _grid.Rows.Clear();
            foreach (var attr in _attributes.OrderBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase))
            {
                _grid.Rows.Add(attr.LogicalName, attr.Kind.ToString(), attr.DisplayValue());
            }
        }

        private static void AddButton(Control parent, string text, EventHandler onClick)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += onClick;
            parent.Controls.Add(button);
        }
    }
}
