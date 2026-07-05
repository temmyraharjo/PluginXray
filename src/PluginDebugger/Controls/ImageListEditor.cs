using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Xrm.Sdk;
using PluginDebugger.Metadata;
using PluginDebugger.Runtime;
using Label = System.Windows.Forms.Label;

namespace PluginDebugger.Controls
{
    /// <summary>One pre/post entity image with its key and typed attributes.</summary>
    internal sealed class ImageEntry
    {
        public bool IsPreImage { get; set; }
        public string Key { get; set; }
        public List<TypedAttribute> Attributes { get; set; } = new List<TypedAttribute>();
    }

    /// <summary>
    /// Manages multiple pre/post images (requirements FR-5.4). Each image is registered under a
    /// user-supplied key and edited with the same typed editor as the Target. Which image types
    /// are allowed is governed by the resolved <see cref="FormShape"/>.
    /// </summary>
    internal sealed class ImageListEditor : UserControl
    {
        private readonly DataGridView _grid;
        private readonly Button _addButton;
        private readonly List<ImageEntry> _entries = new List<ImageEntry>();

        private IOrganizationService _service;
        private MetadataCache _metadata;
        private string _entityName;
        private bool _preAllowed;
        private bool _postAllowed;

        public ImageListEditor()
        {
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, FlowDirection = FlowDirection.LeftToRight };
            _addButton = new Button { Text = "Add image…", AutoSize = true };
            _addButton.Click += (s, e) => AddImage();
            var edit = new Button { Text = "Edit…", AutoSize = true };
            edit.Click += (s, e) => EditSelected();
            var remove = new Button { Text = "Remove", AutoSize = true };
            remove.Click += (s, e) => RemoveSelected();
            toolbar.Controls.Add(_addButton);
            toolbar.Controls.Add(edit);
            toolbar.Controls.Add(remove);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false
            };
            _grid.Columns.Add("type", "Image");
            _grid.Columns.Add("key", "Key");
            _grid.Columns.Add("count", "# attrs");
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) EditSelected(); };

            Controls.Add(_grid);
            Controls.Add(toolbar);
        }

        public IReadOnlyList<ImageEntry> Entries => _entries;

        public void SetContext(IOrganizationService service, MetadataCache metadata, string entityName)
        {
            _service = service;
            _metadata = metadata;
            _entityName = entityName;
        }

        public void Clear()
        {
            _entries.Clear();
            RefreshGrid();
        }

        /// <summary>Replaces the current images (used by full-context import, §4.11).</summary>
        public void SetEntries(IEnumerable<ImageEntry> entries)
        {
            _entries.Clear();
            _entries.AddRange(entries);
            RefreshGrid();
        }

        /// <summary>Constrains which image types may be added and prunes any now-disallowed entries.</summary>
        public void SetAllowed(bool preAllowed, bool postAllowed)
        {
            _preAllowed = preAllowed;
            _postAllowed = postAllowed;
            _addButton.Enabled = preAllowed || postAllowed;
            _entries.RemoveAll(e => (e.IsPreImage && !preAllowed) || (!e.IsPreImage && !postAllowed));
            RefreshGrid();
        }

        private void AddImage()
        {
            if (!EnsureReady())
            {
                return;
            }

            var entry = new ImageEntry { IsPreImage = _preAllowed, Key = _preAllowed ? "PreImage" : "PostImage" };
            using (var dialog = new ImageEditDialog(_service, _metadata, _entityName, _preAllowed, _postAllowed, entry))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _entries.Add(entry);
                    RefreshGrid();
                }
            }
        }

        private void EditSelected()
        {
            if (!EnsureReady())
            {
                return;
            }

            var entry = SelectedEntry();
            if (entry == null)
            {
                return;
            }

            using (var dialog = new ImageEditDialog(_service, _metadata, _entityName, _preAllowed, _postAllowed, entry))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    RefreshGrid();
                }
            }
        }

        private void RemoveSelected()
        {
            var entry = SelectedEntry();
            if (entry != null)
            {
                _entries.Remove(entry);
                RefreshGrid();
            }
        }

        private ImageEntry SelectedEntry()
        {
            return _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0].Tag as ImageEntry;
        }

        private bool EnsureReady()
        {
            if (_service == null || _metadata == null || string.IsNullOrWhiteSpace(_entityName))
            {
                MessageBox.Show(this, "Connect and enter a valid primary entity first.", "Images",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            return true;
        }

        private void RefreshGrid()
        {
            _grid.Rows.Clear();
            foreach (var entry in _entries)
            {
                var index = _grid.Rows.Add(entry.IsPreImage ? "PreImage" : "PostImage", entry.Key, entry.Attributes.Count);
                _grid.Rows[index].Tag = entry;
            }
        }

        /// <summary>Dialog that edits one image: its type, key, and typed attributes.</summary>
        private sealed class ImageEditDialog : Form
        {
            private readonly ImageEntry _entry;
            private readonly ComboBox _typeCombo;
            private readonly TextBox _keyBox;
            private readonly TypedAttributeEditor _editor;

            public ImageEditDialog(IOrganizationService service, MetadataCache metadata, string entityName,
                bool preAllowed, bool postAllowed, ImageEntry entry)
            {
                _entry = entry;

                Text = "Edit image";
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(560, 460);

                var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 64, ColumnCount = 2, Padding = new Padding(6) };
                top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
                top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                top.Controls.Add(new Label { Text = "Image:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
                _typeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
                if (preAllowed) _typeCombo.Items.Add("PreImage");
                if (postAllowed) _typeCombo.Items.Add("PostImage");
                _typeCombo.SelectedItem = entry.IsPreImage && preAllowed ? "PreImage" : (postAllowed ? "PostImage" : (preAllowed ? "PreImage" : null));
                top.Controls.Add(_typeCombo, 1, 0);

                top.Controls.Add(new Label { Text = "Key:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
                _keyBox = new TextBox { Text = entry.Key, Width = 240 };
                top.Controls.Add(_keyBox, 1, 1);

                _editor = new TypedAttributeEditor { Dock = DockStyle.Fill };
                _editor.SetContext(service, metadata, entityName);
                _editor.SetAttributes(entry.Attributes);

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6) };
                var ok = new Button { Text = "OK", AutoSize = true };
                ok.Click += OnOk;
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);

                Controls.Add(_editor);
                Controls.Add(buttons);
                Controls.Add(top);
                CancelButton = cancel;
            }

            private void OnOk(object sender, EventArgs e)
            {
                if (string.IsNullOrWhiteSpace(_keyBox.Text))
                {
                    MessageBox.Show(this, "Enter an image key (how the plugin reads it, e.g. PreImage).",
                        "Edit image", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                _entry.IsPreImage = (_typeCombo.SelectedItem as string) == "PreImage";
                _entry.Key = _keyBox.Text.Trim();
                _entry.Attributes = _editor.Attributes.ToList();
                DialogResult = DialogResult.OK;
                Close();
            }
        }
    }
}
