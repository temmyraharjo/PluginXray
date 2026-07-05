using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using PluginDebugger.Runtime;

namespace PluginDebugger.Dialogs
{
    /// <summary>
    /// After a record is fetched for hydration, lets the user choose which attributes to bring
    /// into the Target/image editor (requirements FR-2.4 / FR-3.4). For Update this is how the
    /// user keeps the Target to just the "changed" attributes rather than the whole row.
    /// </summary>
    internal sealed class HydrateAttributesDialog : Form
    {
        private readonly CheckedListBox _list;
        private readonly List<TypedAttribute> _candidates;

        public HydrateAttributesDialog(IReadOnlyList<TypedAttribute> candidates, bool checkAllByDefault)
        {
            _candidates = candidates.OrderBy(c => c.LogicalName).ToList();

            Text = "Choose attributes to hydrate";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 460);

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(8, 8, 8, 0),
                Text = "Select which fetched attributes to load into the editor."
            };

            _list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            foreach (var attr in _candidates)
            {
                _list.Items.Add($"{attr.LogicalName} = {attr.DisplayValue()}", checkAllByDefault);
            }

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6) };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var none = new Button { Text = "None", AutoSize = true };
            none.Click += (s, e) => SetAll(false);
            var all = new Button { Text = "All", AutoSize = true };
            all.Click += (s, e) => SetAll(true);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(none);
            buttons.Controls.Add(all);

            Controls.Add(_list);
            Controls.Add(buttons);
            Controls.Add(hint);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public List<TypedAttribute> SelectedAttributes =>
            Enumerable.Range(0, _candidates.Count)
                .Where(i => _list.GetItemChecked(i))
                .Select(i => _candidates[i])
                .ToList();

        private void SetAll(bool value)
        {
            for (var i = 0; i < _list.Items.Count; i++)
            {
                _list.SetItemChecked(i, value);
            }
        }
    }
}
