using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using PluginDebugger.Runtime;

namespace PluginDebugger.Dialogs
{
    /// <summary>
    /// Editor for an entity's <c>FormattedValues</c> (requirements FR-5.7). Display strings keyed by
    /// attribute logical name, edited either <b>manually</b> in an editable grid or as <b>JSON</b>
    /// (a plain <c>{"&lt;attr&gt;":"&lt;display&gt;"}</c> object). Values are always strings — no
    /// metadata or typed shape is involved.
    /// </summary>
    internal sealed class FormattedValuesDialog : Form
    {
        private readonly DataGridView _grid;

        public Dictionary<string, string> Values { get; private set; }

        public FormattedValuesDialog(IEnumerable<KeyValuePair<string, string>> initial)
        {
            Text = "Formatted values";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(480, 380);
            MinimumSize = new Size(360, 260);

            var hint = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 40,
                Padding = new Padding(8, 6, 8, 0),
                Text = "Display strings the plugin reads via entity.GetFormattedAttributeValue(\"<attr>\").\n" +
                       "Key = attribute logical name; value = display string."
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = true
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "key", HeaderText = "Attribute", FillWeight = 40 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "value", HeaderText = "Formatted value", FillWeight = 60 });

            if (initial != null)
            {
                foreach (var pair in initial)
                {
                    _grid.Rows.Add(pair.Key, pair.Value);
                }
            }

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42, Padding = new Padding(6) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = "OK", AutoSize = true };
            ok.Click += OnOk;
            var json = new Button { Text = "Edit as JSON…", AutoSize = true };
            json.Click += (s, e) => EditJson();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(json);

            Controls.Add(_grid);
            Controls.Add(buttons);
            Controls.Add(hint);
            CancelButton = cancel;
        }

        private void EditJson()
        {
            if (!TryReadGrid(out var current, out var error))
            {
                MessageBox.Show(this, error, "Formatted values", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var dialog = new JsonForm(FormattedValueJson.Export(current)))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _grid.Rows.Clear();
                    foreach (var pair in dialog.Result)
                    {
                        _grid.Rows.Add(pair.Key, pair.Value);
                    }
                }
            }
        }

        private void OnOk(object sender, EventArgs e)
        {
            if (!TryReadGrid(out var values, out var error))
            {
                MessageBox.Show(this, error, "Formatted values", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Values = values;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Reads the grid into a map, rejecting blank keys with a value and duplicate keys.</summary>
        private bool TryReadGrid(out Dictionary<string, string> values, out string error)
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow)
                {
                    continue;
                }

                var key = Convert.ToString(row.Cells["key"].Value)?.Trim();
                var value = Convert.ToString(row.Cells["value"].Value) ?? string.Empty;
                if (string.IsNullOrEmpty(key))
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        error = "A formatted value has no attribute key.";
                        return false;
                    }
                    continue; // fully blank row
                }
                if (values.ContainsKey(key))
                {
                    error = $"Duplicate attribute key '{key}'.";
                    return false;
                }
                values[key] = value;
            }

            error = null;
            return true;
        }

        /// <summary>A minimal JSON text editor for the plain string→string FormattedValues map.</summary>
        private sealed class JsonForm : Form
        {
            private readonly TextBox _textBox;
            private readonly Label _errorLabel;

            public Dictionary<string, string> Result { get; private set; }

            public JsonForm(string initialJson)
            {
                Text = "Formatted values (JSON)";
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(460, 360);
                MinimumSize = new Size(320, 240);

                var hint = new Label
                {
                    Dock = DockStyle.Top,
                    AutoSize = false,
                    Height = 24,
                    Padding = new Padding(8, 4, 8, 0),
                    Text = "Plain JSON, e.g. {\"statuscode\":\"Active\",\"revenue\":\"$1,234.56\"}."
                };

                _textBox = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    AcceptsReturn = true,
                    AcceptsTab = true,
                    Font = new Font("Consolas", 9f),
                    Text = string.IsNullOrWhiteSpace(initialJson) ? "{\n}" : initialJson
                };

                _errorLabel = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 40, ForeColor = Color.Firebrick, Padding = new Padding(8, 2, 8, 2) };

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var apply = new Button { Text = "Apply", AutoSize = true };
                apply.Click += (s, e) => Apply();
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(apply);

                Controls.Add(_textBox);
                Controls.Add(_errorLabel);
                Controls.Add(buttons);
                Controls.Add(hint);
                CancelButton = cancel;
            }

            private void Apply()
            {
                var result = FormattedValueJson.Import(_textBox.Text);
                if (!result.Success)
                {
                    _errorLabel.Text = "Rejected: " + string.Join("; ", result.Errors);
                    return;
                }

                Result = result.Values;
                DialogResult = DialogResult.OK;
                Close();
            }
        }
    }
}
