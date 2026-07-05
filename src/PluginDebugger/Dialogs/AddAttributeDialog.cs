using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using PluginDebugger.Metadata;
using PluginDebugger.Runtime;
using Label = System.Windows.Forms.Label;

namespace PluginDebugger.Dialogs
{
    /// <summary>
    /// Adds (or edits) one attribute with a type-appropriate input driven by table metadata
    /// (requirements FR-5.2 / FR-5.3). The editor is add-only — the user picks just the
    /// attributes they care about; the whole schema is never rendered (FR-5.1).
    /// </summary>
    internal sealed class AddAttributeDialog : Form
    {
        private readonly IOrganizationService _service;
        private readonly MetadataCache _metadata;
        private readonly string _entityName;

        private readonly ComboBox _attrCombo;
        private readonly Panel _valuePanel;
        private Func<TypedAttribute> _valueReader;

        // Lookup state (set by the record picker).
        private Guid _lookupId;
        private string _lookupEntity;

        public TypedAttribute Result { get; private set; }

        public AddAttributeDialog(IOrganizationService service, MetadataCache metadata, string entityName, TypedAttribute existing = null)
        {
            _service = service;
            _metadata = metadata;
            _entityName = entityName;

            Text = existing == null ? "Add attribute" : "Edit attribute";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(440, 230);

            Controls.Add(new Label { Text = "Attribute:", AutoSize = true, Location = new Point(12, 15) });
            // Editable + type-to-filter, like the Table picker: the list can be long, so let the user
            // type to narrow it rather than scroll a fixed dropdown.
            _attrCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
                Location = new Point(90, 12),
                Size = new Size(338, 24),
                Sorted = true
            };
            Controls.Add(_attrCombo);

            Controls.Add(new Label { Text = "Value:", AutoSize = true, Location = new Point(12, 56) });
            _valuePanel = new Panel { Location = new Point(90, 50), Size = new Size(338, 130) };
            Controls.Add(_valuePanel);

            var ok = new Button { Text = "OK", Location = new Point(272, 196), Size = new Size(75, 26) };
            ok.Click += OnOk;
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(353, 196), Size = new Size(75, 26) };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            PopulateAttributes(existing);
            // With an editable, auto-completing combo the selection can change via typing (which often
            // leaves SelectedItem null) as well as picking — so resolve on both change and focus-leave.
            _attrCombo.SelectedIndexChanged += (s, e) => OnAttributeChanged();
            _attrCombo.Leave += (s, e) => OnAttributeChanged();

            if (existing != null)
            {
                _builtAttribute = SelectedAttribute()?.LogicalName;
                BuildValueEditor(SelectedAttribute(), existing);
            }
            else if (_attrCombo.Items.Count > 0)
            {
                _attrCombo.SelectedIndex = 0;
            }
        }

        /// <summary>The logical name whose value editor is currently built, so we rebuild only on a real change.</summary>
        private string _builtAttribute;

        /// <summary>Rebuilds the value editor when the resolved attribute actually changes.</summary>
        private void OnAttributeChanged()
        {
            var attr = SelectedAttribute();
            var logical = attr?.LogicalName;
            if (string.Equals(logical, _builtAttribute, StringComparison.OrdinalIgnoreCase))
            {
                return; // unchanged — keep the value the user may be entering
            }

            _builtAttribute = logical;
            BuildValueEditor(attr, null);
        }

        private sealed class AttrItem
        {
            public AttributeMetadata Meta;
            public override string ToString() => MetadataHelpers.DisplayName(Meta);
        }

        private void PopulateAttributes(TypedAttribute existing)
        {
            var attrs = _metadata.GetWritableAttributes(_entityName);
            foreach (var attr in attrs)
            {
                _attrCombo.Items.Add(new AttrItem { Meta = attr });
            }

            if (existing != null)
            {
                var match = _attrCombo.Items.Cast<AttrItem>()
                    .FirstOrDefault(i => string.Equals(i.Meta.LogicalName, existing.LogicalName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    _attrCombo.SelectedItem = match;
                }
                // Once editing, the attribute itself is fixed.
                _attrCombo.Enabled = false;
            }
        }

        private AttributeMetadata SelectedAttribute()
        {
            // Best case: an item is actually selected.
            if (_attrCombo.SelectedItem is AttrItem selected)
            {
                return selected.Meta;
            }

            // Type-to-filter often leaves SelectedItem null while Text holds the item's display
            // string ("Display Name [logicalname]"). Match the text back to an item.
            var text = _attrCombo.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            foreach (var obj in _attrCombo.Items)
            {
                if (obj is AttrItem item &&
                    (string.Equals(item.ToString(), text, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.Meta.LogicalName, text, StringComparison.OrdinalIgnoreCase)))
                {
                    return item.Meta;
                }
            }

            // Fall back to the "[logicalname]" suffix embedded in the display string.
            var open = text.LastIndexOf('[');
            var close = text.LastIndexOf(']');
            if (open >= 0 && close > open)
            {
                var logical = text.Substring(open + 1, close - open - 1).Trim();
                return _attrCombo.Items.Cast<AttrItem>()
                    .FirstOrDefault(i => string.Equals(i.Meta.LogicalName, logical, StringComparison.OrdinalIgnoreCase))?.Meta;
            }

            return null;
        }

        private void BuildValueEditor(AttributeMetadata attr, TypedAttribute existing)
        {
            _valuePanel.Controls.Clear();
            _valueReader = null;
            _lookupId = Guid.Empty;
            _lookupEntity = null;

            if (attr == null)
            {
                return;
            }

            var kind = AttributeTypeMapper.FromMetadata(attr) ?? AttributeEditorKind.String;
            var name = attr.LogicalName;

            switch (kind)
            {
                case AttributeEditorKind.String:
                case AttributeEditorKind.Memo:
                {
                    var box = NewTextBox(kind == AttributeEditorKind.Memo);
                    box.Text = existing?.Value as string ?? string.Empty;
                    _valueReader = () => new TypedAttribute(name, kind, box.Text);
                    break;
                }
                case AttributeEditorKind.Boolean:
                {
                    var check = new CheckBox { Text = "true", AutoSize = true, Location = new Point(0, 4), Checked = existing?.Value as bool? ?? false };
                    check.CheckedChanged += (s, e) => check.Text = check.Checked ? "true" : "false";
                    _valuePanel.Controls.Add(check);
                    _valueReader = () => new TypedAttribute(name, kind, check.Checked);
                    break;
                }
                case AttributeEditorKind.WholeNumber:
                case AttributeEditorKind.BigInt:
                case AttributeEditorKind.Decimal:
                case AttributeEditorKind.Double:
                case AttributeEditorKind.Money:
                {
                    var box = NewTextBox(false);
                    box.Text = existing != null ? Convert.ToString(existing.Value, CultureInfo.InvariantCulture) : string.Empty;
                    _valueReader = () => new TypedAttribute(name, kind, ParseNumber(kind, box.Text));
                    break;
                }
                case AttributeEditorKind.DateTime:
                {
                    var picker = new DateTimePicker
                    {
                        Format = DateTimePickerFormat.Custom,
                        CustomFormat = "yyyy-MM-dd HH:mm:ss",
                        ShowUpDown = true,
                        Location = new Point(0, 2),
                        Size = new Size(200, 24),
                        Value = existing?.Value as DateTime? ?? DateTime.Now
                    };
                    _valuePanel.Controls.Add(picker);
                    _valueReader = () => new TypedAttribute(name, kind, picker.Value);
                    break;
                }
                case AttributeEditorKind.OptionSet:
                {
                    var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(0, 2), Size = new Size(330, 24) };
                    var options = MetadataHelpers.GetOptions(attr);
                    foreach (var option in options)
                    {
                        combo.Items.Add(option);
                    }
                    if (existing?.Value is int existingValue)
                    {
                        combo.SelectedItem = options.FirstOrDefault(o => o.Value == existingValue);
                    }
                    _valuePanel.Controls.Add(combo);
                    _valueReader = () => combo.SelectedItem is OptionChoice c
                        ? new TypedAttribute(name, kind, c.Value)
                        : throw new FormatException("Select an option.");
                    break;
                }
                case AttributeEditorKind.MultiSelectOptionSet:
                {
                    var list = new CheckedListBox { Location = new Point(0, 2), Size = new Size(330, 120), CheckOnClick = true };
                    var options = MetadataHelpers.GetOptions(attr);
                    var preselected = new HashSet<int>(existing?.Value as IEnumerable<int> ?? Enumerable.Empty<int>());
                    foreach (var option in options)
                    {
                        list.Items.Add(option, preselected.Contains(option.Value));
                    }
                    _valuePanel.Controls.Add(list);
                    _valueReader = () => new TypedAttribute(name, kind, list.CheckedItems.Cast<OptionChoice>().Select(c => c.Value).ToList());
                    break;
                }
                case AttributeEditorKind.Guid:
                {
                    var box = NewTextBox(false);
                    box.Text = existing?.Value?.ToString() ?? Guid.NewGuid().ToString();
                    _valueReader = () => new TypedAttribute(name, kind, Guid.Parse(box.Text.Trim()));
                    break;
                }
                case AttributeEditorKind.Lookup:
                {
                    var display = new TextBox { ReadOnly = true, Location = new Point(0, 2), Size = new Size(240, 24) };
                    var pick = new Button { Text = "Pick…", Location = new Point(248, 1), Size = new Size(80, 26) };
                    if (existing != null && existing.Value is Guid existingId)
                    {
                        _lookupId = existingId;
                        _lookupEntity = existing.LookupEntity;
                        display.Text = $"{_lookupEntity}: {_lookupId}";
                    }
                    pick.Click += (s, e) => PickLookup(attr, display);
                    _valuePanel.Controls.Add(display);
                    _valuePanel.Controls.Add(pick);
                    _valueReader = () => _lookupEntity != null && _lookupId != Guid.Empty
                        ? new TypedAttribute(name, kind, _lookupId, _lookupEntity)
                        : throw new FormatException("Pick a record for the lookup.");
                    break;
                }
            }
        }

        private void PickLookup(AttributeMetadata attr, TextBox display)
        {
            var targets = MetadataHelpers.GetLookupTargets(attr);
            var entity = PolymorphicTargetDialog.Choose(this, attr.LogicalName, targets);
            if (entity == null)
            {
                return;
            }

            using (var picker = new RecordPickerDialog(_service, _metadata, entity))
            {
                if (picker.ShowDialog(this) == DialogResult.OK)
                {
                    _lookupEntity = entity;
                    _lookupId = picker.SelectedId;
                    display.Text = $"{entity}: {picker.SelectedName} ({picker.SelectedId})";
                }
            }
        }

        private TextBox NewTextBox(bool multiline)
        {
            var box = new TextBox
            {
                Location = new Point(0, 2),
                Size = new Size(330, multiline ? 120 : 24),
                Multiline = multiline,
                ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None
            };
            _valuePanel.Controls.Add(box);
            return box;
        }

        private static object ParseNumber(AttributeEditorKind kind, string text)
        {
            text = (text ?? string.Empty).Trim();
            switch (kind)
            {
                case AttributeEditorKind.WholeNumber:
                    return int.Parse(text, CultureInfo.InvariantCulture);
                case AttributeEditorKind.BigInt:
                    return long.Parse(text, CultureInfo.InvariantCulture);
                case AttributeEditorKind.Double:
                    return double.Parse(text, CultureInfo.InvariantCulture);
                default: // Decimal / Money
                    return decimal.Parse(text, CultureInfo.InvariantCulture);
            }
        }

        private void OnOk(object sender, EventArgs e)
        {
            if (_valueReader == null)
            {
                MessageBox.Show(this, "Select an attribute.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Result = _valueReader();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Invalid value: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
