using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using PluginDebugger.Runtime;

namespace PluginDebugger.Dialogs
{
    /// <summary>
    /// An editable JSON view of an attribute set (requirements FR-5.6). Pre-filled with the
    /// current attributes (typed-envelope format), it lets the user add/edit attributes as JSON
    /// and apply them back — either replacing or merging. Parsing is validated against table
    /// metadata; on error the messages are shown inline and the text is kept.
    /// </summary>
    internal sealed class EditJsonDialog : Form
    {
        internal enum ApplyMode { Replace, Merge }

        private readonly TextBox _textBox;
        private readonly Label _errorLabel;
        private readonly Func<string, AttributeEditorKind?> _kindResolver;

        public List<TypedAttribute> ResultAttributes { get; private set; }
        public ApplyMode Mode { get; private set; }

        public EditJsonDialog(string initialJson, Func<string, AttributeEditorKind?> kindResolver)
        {
            _kindResolver = kindResolver;

            Text = "Edit attributes as JSON";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(580, 500);
            MinimumSize = new Size(420, 320);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var hint = new Label
            {
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 6),
                Text = "Typed-envelope JSON. Unambiguous scalars are plain (\"name\":\"x\", \"done\":true, \"count\":5);\n" +
                       "ambiguous types use an envelope, e.g. {\"statuscode\":{\"t\":\"optionset\",\"v\":2}}, " +
                       "{\"primarycontactid\":{\"t\":\"lookup\",\"entity\":\"contact\",\"v\":\"<guid>\"}}."
            };

            _textBox = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9f),
                AcceptsReturn = true,
                AcceptsTab = true,
                Text = string.IsNullOrWhiteSpace(initialJson) ? "{\n}" : initialJson
            };

            _errorLabel = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(0, 6, 0, 6), MaximumSize = new Size(560, 0) };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var merge = new Button { Text = "Apply (Merge)", AutoSize = true };
            merge.Click += (s, e) => Apply(ApplyMode.Merge);
            var replace = new Button { Text = "Apply (Replace)", AutoSize = true };
            replace.Click += (s, e) => Apply(ApplyMode.Replace);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(merge);
            buttons.Controls.Add(replace);

            layout.Controls.Add(hint, 0, 0);
            layout.Controls.Add(_textBox, 0, 1);
            layout.Controls.Add(_errorLabel, 0, 2);
            layout.Controls.Add(buttons, 0, 3);

            Controls.Add(layout);
            CancelButton = cancel;
        }

        private void Apply(ApplyMode mode)
        {
            var result = AttributeJson.Import(_textBox.Text, _kindResolver);
            if (!result.Success)
            {
                _errorLabel.Text = "Rejected:\n" + string.Join("\n", result.Errors);
                return;
            }

            ResultAttributes = result.Attributes;
            Mode = mode;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
