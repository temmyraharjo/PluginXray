using System;
using System.Drawing;
using System.Windows.Forms;

namespace PluginDebugger.Dialogs
{
    /// <summary>
    /// Asks the user which target entity to use for a polymorphic lookup (Customer / Owner /
    /// any lookup with multiple Targets) rather than assuming one (requirements FR-5.3).
    /// </summary>
    internal sealed class PolymorphicTargetDialog : Form
    {
        private readonly ComboBox _combo;

        public PolymorphicTargetDialog(string attributeName, string[] targets)
        {
            Text = "Choose lookup target";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(360, 110);

            var label = new Label
            {
                Text = $"'{attributeName}' is a polymorphic lookup. Choose the target entity:",
                AutoSize = false,
                Location = new Point(12, 12),
                Size = new Size(336, 32)
            };

            _combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(12, 48),
                Size = new Size(336, 24)
            };
            _combo.Items.AddRange(targets);
            if (_combo.Items.Count > 0)
            {
                _combo.SelectedIndex = 0;
            }

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(192, 78), Size = new Size(75, 24) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(273, 78), Size = new Size(75, 24) };

            Controls.Add(label);
            Controls.Add(_combo);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public string SelectedTarget => _combo.SelectedItem as string;

        /// <summary>Resolves the target entity, only prompting when the lookup is genuinely polymorphic.</summary>
        public static string Choose(IWin32Window owner, string attributeName, string[] targets)
        {
            if (targets == null || targets.Length == 0)
            {
                return null;
            }
            if (targets.Length == 1)
            {
                return targets[0];
            }

            using (var dialog = new PolymorphicTargetDialog(attributeName, targets))
            {
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedTarget : null;
            }
        }
    }
}
