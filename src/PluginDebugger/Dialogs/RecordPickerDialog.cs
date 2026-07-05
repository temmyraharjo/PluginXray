using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PluginDebugger.Metadata;
using Label = System.Windows.Forms.Label;

namespace PluginDebugger.Dialogs
{
    /// <summary>
    /// Lets the user pick an existing record of a given entity to seed a lookup or a Delete
    /// Target (requirements FR-5.3). Searches by the entity's primary name attribute.
    /// </summary>
    internal sealed class RecordPickerDialog : Form
    {
        private readonly IOrganizationService _service;
        private readonly MetadataCache _metadata;
        private readonly string _entityName;
        private readonly string _primaryName;
        private readonly string _primaryId;

        private readonly TextBox _searchBox;
        private readonly DataGridView _grid;

        public Guid SelectedId { get; private set; }
        public string SelectedName { get; private set; }

        public RecordPickerDialog(IOrganizationService service, MetadataCache metadata, string entityName)
        {
            _service = service;
            _metadata = metadata;
            _entityName = entityName;
            _primaryName = metadata.PrimaryNameAttribute(entityName);
            _primaryId = metadata.PrimaryIdAttribute(entityName);

            Text = $"Pick a {entityName} record";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 420);
            MinimizeBox = false;

            var searchLabel = new Label { Text = "Search:", AutoSize = true, Location = new Point(12, 15) };
            _searchBox = new TextBox { Location = new Point(70, 12), Size = new Size(330, 24) };
            var searchButton = new Button { Text = "Search", Location = new Point(410, 11), Size = new Size(95, 26) };
            searchButton.Click += (s, e) => Search();
            _searchBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(); } };

            _grid = new DataGridView
            {
                Location = new Point(12, 46),
                Size = new Size(493, 330),
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false
            };
            _grid.Columns.Add("name", "Name");
            _grid.Columns.Add("id", "Id");
            _grid.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) Accept(); };

            var ok = new Button { Text = "Select", DialogResult = DialogResult.None, Location = new Point(349, 384), Size = new Size(75, 26) };
            ok.Click += (s, e) => Accept();
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(430, 384), Size = new Size(75, 26) };

            Controls.Add(searchLabel);
            Controls.Add(_searchBox);
            Controls.Add(searchButton);
            Controls.Add(_grid);
            Controls.Add(ok);
            Controls.Add(cancel);
            CancelButton = cancel;

            Load += (s, e) => Search();
        }

        private void Search()
        {
            try
            {
                var query = new QueryExpression(_entityName)
                {
                    ColumnSet = new ColumnSet(_primaryName),
                    TopCount = 50,
                    Orders = { new OrderExpression(_primaryName, OrderType.Ascending) }
                };

                var term = _searchBox.Text.Trim();
                if (!string.IsNullOrEmpty(term))
                {
                    query.Criteria.AddCondition(_primaryName, ConditionOperator.Like, "%" + term + "%");
                }

                var results = _service.RetrieveMultiple(query);

                _grid.Rows.Clear();
                foreach (var record in results.Entities)
                {
                    _grid.Rows.Add(record.GetAttributeValue<string>(_primaryName) ?? "(no name)", record.Id.ToString());
                }

                if (_grid.Rows.Count > 0)
                {
                    _grid.Rows[0].Selected = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Search failed: " + ex.Message, "Record picker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Accept()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                return;
            }

            var row = _grid.SelectedRows[0];
            SelectedName = row.Cells[0].Value?.ToString();
            if (Guid.TryParse(row.Cells[1].Value?.ToString(), out var id))
            {
                SelectedId = id;
                DialogResult = DialogResult.OK;
                Close();
            }
        }
    }
}
