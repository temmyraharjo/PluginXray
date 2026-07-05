using System;
using System.Drawing;
using System.Windows.Forms;

namespace PluginDebugger.Dialogs
{
    /// <summary>
    /// Collects a pasted serialized <c>IExecutionContext</c> JSON (requirements §4.11). Pre-fills from
    /// the clipboard when it already holds something JSON-shaped, so the common flow is: copy from the
    /// trace log → open → OK.
    /// </summary>
    internal sealed class PasteContextDialog : Form
    {
        private readonly TextBox _textBox;

        public PasteContextDialog()
        {
            Text = "Paste execution context (JSON)";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 520);
            MinimumSize = new Size(420, 320);

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(8, 6, 8, 0),
                Text = "Paste the serialized IExecutionContext JSON (as captured by the Plugin Registration Tool " +
                       "profiler or a plugin trace). It populates the whole form; it does not run the plugin."
            };

            _textBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                AcceptsReturn = true,
                AcceptsTab = true,
                Font = new Font("Consolas", 9f)
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
            var ok = new Button { Text = "Import", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);

            Controls.Add(_textBox);
            Controls.Add(buttons);
            Controls.Add(hint);
            AcceptButton = ok;
            CancelButton = cancel;

            TryPrefillFromClipboard();
        }

        public string Json => _textBox.Text;

        private void TryPrefillFromClipboard()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("{"))
                    {
                        _textBox.Text = text;
                        _textBox.SelectAll();
                    }
                }
            }
            catch
            {
                // Clipboard access can throw transiently; a pre-fill is a nicety, not required.
            }
        }
    }
}
