using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using McTools.Xrm.Connection;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PluginDebugger.Controls;
using PluginDebugger.Debugging;
using PluginDebugger.Dialogs;
using PluginDebugger.Metadata;
using PluginDebugger.Runtime;
using XrmToolBox.Extensibility;
// Microsoft.Xrm.Sdk also defines a Label type; in this WinForms control "Label" means the UI control.
using Label = System.Windows.Forms.Label;
// McTools.Xrm.Connection also defines a MetadataCache; disambiguate to ours.
using MetadataCache = PluginDebugger.Metadata.MetadataCache;

namespace PluginDebugger
{
    /// <summary>
    /// Keystone UI (requirements §8 step 1–2): pick an assembly + plugin type, choose a
    /// message/stage/mode, supply a minimal Target, and Trigger. Proves the hard part end
    /// to end — the original dll stays unlocked, a fresh child AppDomain runs per click,
    /// symbols are reported, and trace/SDK/exception output streams to the run log.
    ///
    /// The rich typed attribute/image editor, the full form-shape engine, and metadata-driven
    /// table/record pickers are deliberately later steps; this control keeps the Target editor
    /// to plain string attributes so the loop can be validated first.
    /// </summary>
    public class PluginDebuggerControl : PluginControlBase
    {
        private Label _bannerLabel;
        private TextBox _assemblyPathBox;
        private ComboBox _typeCombo;
        private TextBox _unsecureBox;
        private TextBox _secureBox;
        // Sentinel item in _messageCombo that unlocks the free-text MessageName box.
        private const string OtherMessageItem = "Other…";
        private ComboBox _messageCombo;
        private TextBox _customMessageBox;
        private ComboBox _stageCombo;
        private ComboBox _modeCombo;
        private ComboBox _tableCombo;
        private Button _hydrateButton;
        private TypedAttributeEditor _targetEditor;
        private Label _targetGridLabel;
        private TextBox _targetIdBox;
        private Label _targetIdLabel;
        private TextBox _outputIdBox;
        private Label _outputIdLabel;
        private ImageListEditor _imageEditor;
        private Label _imageLabel;

        // Execution context editor (§4.4)
        private ComboBox _contextModeCombo;
        private TextBox _depthBox;
        private TextBox _userIdBox;
        private TextBox _initiatingUserIdBox;
        private TextBox _businessUnitIdBox;
        private TextBox _organizationIdBox;
        private TextBox _correlationIdBox;
        private DataGridView _sharedVarsGrid;
        private DataGridView _inputParamsGrid;

        // Custom workflow activity editor (§4.12)
        private Label _workflowHeaderLabel;
        private Label _workflowHeaderFiller;
        private DataGridView _workflowArgsGrid;
        private Label _workflowArgsLabel;
        private TextBox _stageNameBox;
        private Label _stageNameLabel;
        private ComboBox _workflowModeCombo;
        private Label _workflowModeLabel;

        // Visual Studio attach (§4.9)
        private ComboBox _vsCombo;

        private Button _triggerButton;
        private Label _symbolsLabel;
        private RichTextBox _logBox;

        private readonly RunLogSink _logSink = new RunLogSink();
        private MetadataCache _metadata;
        private Guid _defaultUserId;
        private Guid _defaultBusinessUnitId;
        private Guid _defaultOrganizationId;
        private Guid? _hydratedPrimaryId;

        public PluginDebuggerControl()
        {
            BuildUi();
            _logSink.EntryLogged += OnEntryLogged;
            ApplyFormShape();
            UpdateBanner();
        }

        // ---- connection ---------------------------------------------------------------------

        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            // Build connection-derived state BEFORE base runs. When a connection-requiring control
            // was used while disconnected, ExecuteMethod queued its action and base.UpdateConnection
            // replays it here — the replay must already see a ready metadata cache and editor
            // context, so establish them first (using newService, since Service isn't set yet).
            _metadata = newService != null ? new MetadataCache(newService) : null;
            SetEditorContext(newService);

            base.UpdateConnection(newService, detail, actionName, parameter);

            UpdateBanner();
            LoadConnectionDefaults();
            LoadTables();
        }

        /// <summary>
        /// Resolves the connection user / business unit / organization via WhoAmI and uses them as
        /// defaults for the context editor (requirements FR-4.2). Runs off the UI thread.
        /// </summary>
        private async void LoadConnectionDefaults()
        {
            if (Service == null)
            {
                return;
            }

            try
            {
                var service = Service;
                var who = await Task.Run(() => (WhoAmIResponse)service.Execute(new WhoAmIRequest()));
                _defaultUserId = who.UserId;
                _defaultBusinessUnitId = who.BusinessUnitId;
                _defaultOrganizationId = who.OrganizationId;
                ApplyContextDefaults();
            }
            catch (Exception ex)
            {
                AppendLog(LogCategory.Warning, "Could not resolve the connection user (WhoAmI): " + ex.Message);
            }
        }

        /// <summary>Fills any empty context-id field with the resolved connection default.</summary>
        private void ApplyContextDefaults()
        {
            SetIfEmpty(_userIdBox, _defaultUserId);
            SetIfEmpty(_initiatingUserIdBox, _defaultUserId);
            SetIfEmpty(_businessUnitIdBox, _defaultBusinessUnitId);
            SetIfEmpty(_organizationIdBox, _defaultOrganizationId);
        }

        private static void SetIfEmpty(TextBox box, Guid value)
        {
            if (box != null && string.IsNullOrWhiteSpace(box.Text) && value != Guid.Empty)
            {
                box.Text = value.ToString();
            }
        }

        /// <summary>Pushes the current service, metadata cache and primary entity into the editors.</summary>
        private void UpdateEditorContext() => SetEditorContext(Service);

        private void SetEditorContext(IOrganizationService service)
        {
            var entityName = SelectedEntityName();
            _targetEditor?.SetContext(service, _metadata, entityName);
            _imageEditor?.SetContext(service, _metadata, entityName);
        }

        /// <summary>The selected table's logical name (from the picked item, or free-typed text).</summary>
        private string SelectedEntityName()
        {
            if (_tableCombo == null)
            {
                return null;
            }

            // Best case: the user picked an item from the dropdown.
            if (_tableCombo.SelectedItem is TableItem selected)
            {
                return selected.LogicalName;
            }

            // WinForms quirk: with ListItems auto-complete, typing a value often leaves
            // SelectedItem null while Text holds the item's "logical — display" string. Match the
            // text back to an item, then fall back to parsing the logical part.
            var text = _tableCombo.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            foreach (var obj in _tableCombo.Items)
            {
                if (obj is TableItem item &&
                    (string.Equals(item.LogicalName, text, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.ToString(), text, StringComparison.OrdinalIgnoreCase)))
                {
                    return item.LogicalName;
                }
            }

            var separator = text.IndexOf(" — ", StringComparison.Ordinal);
            return separator > 0 ? text.Substring(0, separator).Trim() : text;
        }

        /// <summary>
        /// Builds a context-id row with a Guid textbox, a "…" picker that searches the given entity,
        /// and a label showing the chosen record's name (requirements FR-4.5).
        /// </summary>
        private TextBox BuildIdPickerRow(TableLayoutPanel layout, string label, string entityLogicalName)
        {
            var box = new TextBox();
            var pick = new Button { Text = "…", AutoSize = true, Margin = new Padding(3, 1, 3, 1) };
            var nameLabel = new Label { AutoSize = true, ForeColor = Color.Gray, Anchor = AnchorStyles.Left, Margin = new Padding(6, 8, 0, 0) };
            pick.Click += (s, e) => PickIdentity(entityLogicalName, box, nameLabel);
            AddRow(layout, label, BuildStretchRow(box, pick, nameLabel));
            return box;
        }

        private void PickIdentity(string entityLogicalName, TextBox box, Label nameLabel)
        {
            // Requires a live connection: ExecuteMethod runs this immediately when connected, or
            // opens XrmToolBox's connection selection dialog and replays it once connected.
            ExecuteMethod(() =>
            {
                if (_metadata == null)
                {
                    _metadata = new MetadataCache(Service);
                }

                try
                {
                    using (var picker = new RecordPickerDialog(Service, _metadata, entityLogicalName))
                    {
                        if (picker.ShowDialog(this) == DialogResult.OK)
                        {
                            box.Text = picker.SelectedId.ToString();
                            nameLabel.Text = picker.SelectedName;
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not open the {entityLogicalName} picker: {ex.Message}",
                        "Pick record", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }

        /// <summary>A table choice: logical name plus display name for the picker.</summary>
        private sealed class TableItem
        {
            public string LogicalName;
            public string DisplayName;
            public override string ToString() =>
                string.IsNullOrEmpty(DisplayName) ? LogicalName : $"{LogicalName} — {DisplayName}";
        }

        /// <summary>Loads the table list into the picker (off the UI thread).</summary>
        private async void LoadTables()
        {
            if (_metadata == null)
            {
                return;
            }

            try
            {
                var metadata = _metadata;
                var items = await Task.Run(() => metadata.GetAllEntities()
                    .Select(e => new TableItem
                    {
                        LogicalName = e.LogicalName,
                        DisplayName = e.DisplayName?.UserLocalizedLabel?.Label
                    })
                    .ToArray());

                var current = _tableCombo.Text;
                _tableCombo.BeginUpdate();
                _tableCombo.Items.Clear();
                _tableCombo.Items.AddRange(items.Cast<object>().ToArray());
                _tableCombo.Text = current;
                _tableCombo.EndUpdate();
                AppendLog(LogCategory.Info, $"Loaded {items.Length} tables.");
            }
            catch (Exception ex)
            {
                AppendLog(LogCategory.Warning, "Could not load table list: " + ex.Message);
            }
        }

        private void OnTableChanged()
        {
            // The table is entity-specific, so previously entered attributes/images no longer apply.
            _hydratedPrimaryId = null;
            _targetEditor?.Clear();
            _imageEditor?.Clear();
            UpdateEditorContext();
        }

        private void UpdateBanner()
        {
            if (_bannerLabel == null)
            {
                return;
            }

            if (ConnectionDetail == null)
            {
                _bannerLabel.Text = "  Not connected — select a connection in XrmToolBox.";
                _bannerLabel.BackColor = Color.FromArgb(80, 80, 80);
                _bannerLabel.ForeColor = Color.White;
                return;
            }

            var url = ConnectionDetail.WebApplicationUrl ?? ConnectionDetail.OrganizationServiceUrl ?? "(unknown url)";
            var isProd = LooksLikeProduction(ConnectionDetail);

            _bannerLabel.Text = (isProd ? "  ⚠ PRODUCTION  —  " : "  ") + ConnectionDetail.ConnectionName + "   |   " + url;
            _bannerLabel.BackColor = isProd ? Color.FromArgb(150, 0, 0) : Color.FromArgb(0, 90, 158);
            _bannerLabel.ForeColor = Color.White;
        }

        // Heuristic for now (§4.1.3). Metadata-based production detection is a later refinement.
        private static bool LooksLikeProduction(ConnectionDetail detail)
        {
            var name = (detail.ConnectionName ?? string.Empty).ToLowerInvariant();
            var url = (detail.WebApplicationUrl ?? detail.OrganizationServiceUrl ?? string.Empty).ToLowerInvariant();
            return name.Contains("prod") || url.Contains("prod");
        }

        // ---- UI construction ---------------------------------------------------------------

        private void BuildUi()
        {
            Dock = DockStyle.Fill;

            _bannerLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 90, 158),
                ForeColor = Color.White
            };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 460
            };

            var config = BuildConfigPanel();
            split.Panel1.Controls.Add(config);
            split.Panel1.AutoScroll = true;

            _logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9f),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                DetectUrls = false
            };
            var logToolbar = BuildLogToolbar();
            split.Panel2.Controls.Add(_logBox);
            split.Panel2.Controls.Add(logToolbar);

            Controls.Add(split);
            Controls.Add(_bannerLabel);
        }

        private Control BuildConfigPanel()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Padding = new Padding(8),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // Assembly
            _assemblyPathBox = new TextBox { ReadOnly = true };
            var browseButton = new Button { Text = "Browse…", AutoSize = true };
            browseButton.Click += OnBrowse;
            var loadTypesButton = new Button { Text = "Load types", AutoSize = true };
            loadTypesButton.Click += OnLoadTypes;
            AddRow(layout, "Plugin assembly", BuildStretchRow(_assemblyPathBox, browseButton, loadTypesButton));

            // Type — plugins and custom workflow activities (§4.12); selection reshapes the form.
            _typeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _typeCombo.SelectedIndexChanged += (s, e) => OnTypeChanged();
            AddRow(layout, "Plugin / activity type", Stretch(_typeCombo));

            // Config
            _unsecureBox = new TextBox();
            AddRow(layout, "Unsecure config", Stretch(_unsecureBox));
            _secureBox = new TextBox();
            AddRow(layout, "Secure config", Stretch(_secureBox));

            // Message / Stage / Mode
            var messagePanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            _messageCombo = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            _messageCombo.Items.AddRange(FormShapeEngine.SupportedMessages.Cast<object>().ToArray());
            _messageCombo.Items.Add(OtherMessageItem);   // arbitrary message name (custom action, SetState, …)
            _messageCombo.SelectedIndex = 0;
            _messageCombo.SelectedIndexChanged += (s, e) => OnMessageChanged();
            // Free-text box shown only when "Other…" is picked; its text becomes MessageName.
            _customMessageBox = new TextBox { Width = 170, Visible = false, Margin = new Padding(6, 3, 3, 3) };
            _customMessageBox.TextChanged += (s, e) => ApplyFormShape();
            messagePanel.Controls.Add(_messageCombo);
            messagePanel.Controls.Add(_customMessageBox);
            AddRow(layout, "Message", messagePanel);

            _stageCombo = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            _stageCombo.Items.AddRange(new object[] { "10 — Pre-validation", "20 — Pre-operation", "40 — Post-operation" });
            _stageCombo.SelectedIndex = 1;
            _stageCombo.SelectedIndexChanged += (s, e) => ApplyFormShape();
            AddRow(layout, "Stage", _stageCombo);

            _modeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _modeCombo.Items.AddRange(new object[] { "Full real (default)", "Read-real / write-mock", "Full mock" });
            _modeCombo.SelectedIndex = 0;
            AddRow(layout, "Execution mode", Stretch(_modeCombo));

            // Full execution-context import (§4.11): paste a serialized IExecutionContext to hydrate
            // the whole form at once.
            var importPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var importButton = new Button { Text = "Paste execution context (JSON)…", AutoSize = true };
            importButton.Click += OnPasteContext;
            importPanel.Controls.Add(importButton);
            AddRow(layout, "Import", importPanel);

            // Table picker (FR-2.1/2.3): the selected table IS the primary entity. Populated from
            // metadata on connect; supports type-to-filter. A free-typed logical name also works.
            _tableCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
                Sorted = true
            };
            _tableCombo.SelectedIndexChanged += (s, e) => OnTableChanged();
            _tableCombo.Leave += (s, e) => UpdateEditorContext();
            _hydrateButton = new Button { Text = "Hydrate from record…", AutoSize = true };
            _hydrateButton.Click += OnHydrate;
            AddRow(layout, "Table", BuildStretchRow(_tableCombo, _hydrateButton));

            // Target — metadata-driven typed attribute editor (Create/Update)
            _targetEditor = new TypedAttributeEditor { Height = 180, Margin = new Padding(3) };
            // Its Add/Edit need metadata: when disconnected, defer through the host's connection flow.
            _targetEditor.RunWithConnection = action => ExecuteMethod(action);
            AddRow(layout, "Target attributes", Stretch(_targetEditor), out _targetGridLabel);

            // Target id (Delete)
            _targetIdBox = new TextBox();
            AddRow(layout, "Target record id", Stretch(_targetIdBox), out _targetIdLabel);

            // OutputParameters["id"] (Create / post-operation only)
            _outputIdBox = new TextBox();
            AddRow(layout, "OutputParameters[\"id\"]", Stretch(_outputIdBox), out _outputIdLabel);

            // Images — multiple pre/post images, each with the same typed editor (FR-5.4)
            _imageEditor = new ImageListEditor { Height = 130, Margin = new Padding(3) };
            _imageEditor.RunWithConnection = action => ExecuteMethod(action);
            AddRow(layout, "Images", Stretch(_imageEditor), out _imageLabel);

            // ----- Custom workflow activity (§4.12) — shown only when a code activity is selected -----
            AddSectionHeader(layout, "Workflow activity", out _workflowHeaderLabel, out _workflowHeaderFiller);

            _workflowArgsGrid = new DataGridView
            {
                Height = 150,
                AllowUserToAddRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            _workflowArgsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "arg", HeaderText = "Argument", ReadOnly = true });
            _workflowArgsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "dir", HeaderText = "Dir", ReadOnly = true, Width = 44 });
            _workflowArgsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "type", HeaderText = "Type", ReadOnly = true });
            _workflowArgsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "value", HeaderText = "Value" });
            AddRow(layout, "Arguments", Stretch(_workflowArgsGrid), out _workflowArgsLabel);

            _stageNameBox = new TextBox();
            AddRow(layout, "StageName", Stretch(_stageNameBox), out _stageNameLabel);

            _workflowModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _workflowModeCombo.Items.AddRange(new object[] { "0 — Background (async)", "1 — Real-time (sync)" });
            _workflowModeCombo.SelectedIndex = 0;
            AddRow(layout, "WorkflowMode", Stretch(_workflowModeCombo), out _workflowModeLabel);

            // ----- Execution context (§4.4) -----
            AddSectionHeader(layout, "Execution context");

            _contextModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _contextModeCombo.Items.AddRange(new object[] { "0 — Synchronous", "1 — Asynchronous" });
            _contextModeCombo.SelectedIndex = 0;
            AddRow(layout, "Mode", Stretch(_contextModeCombo));

            _depthBox = new TextBox { Width = 80, Text = "1" };
            AddRow(layout, "Depth", _depthBox);

            // Identity fields are pickable by finding a record, not only by typing a Guid (FR-4.5).
            _userIdBox = BuildIdPickerRow(layout, "UserId", "systemuser");
            _initiatingUserIdBox = BuildIdPickerRow(layout, "InitiatingUserId", "systemuser");
            _businessUnitIdBox = BuildIdPickerRow(layout, "BusinessUnitId", "businessunit");
            _organizationIdBox = BuildIdPickerRow(layout, "OrganizationId", "organization");

            _correlationIdBox = new TextBox { Text = Guid.NewGuid().ToString() };
            var newCorrelationButton = new Button { Text = "New", AutoSize = true };
            newCorrelationButton.Click += (s, e) => _correlationIdBox.Text = Guid.NewGuid().ToString();
            AddRow(layout, "CorrelationId", BuildStretchRow(_correlationIdBox, newCorrelationButton));

            _sharedVarsGrid = new DataGridView
            {
                Height = 110,
                AllowUserToAddRows = true,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            _sharedVarsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "key", HeaderText = "Key" });
            var typeColumn = new DataGridViewComboBoxColumn { Name = "type", HeaderText = "Type", FlatStyle = FlatStyle.Flat };
            typeColumn.Items.AddRange(SharedVariableValue.TypeNames.Cast<object>().ToArray());
            _sharedVarsGrid.Columns.Add(typeColumn);
            _sharedVarsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "value", HeaderText = "Value" });
            AddRow(layout, "SharedVariables", Stretch(_sharedVarsGrid));

            // Arbitrary InputParameters beyond Target (FR-4.6). Same Key/Type/Value shape as
            // SharedVariables; the value cell format for EntityReference is "logical:guid".
            _inputParamsGrid = new DataGridView
            {
                Height = 110,
                AllowUserToAddRows = true,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            _inputParamsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "key", HeaderText = "Key" });
            var inputTypeColumn = new DataGridViewComboBoxColumn { Name = "type", HeaderText = "Type", FlatStyle = FlatStyle.Flat };
            inputTypeColumn.Items.AddRange(InputParameterValue.TypeNames.Cast<object>().ToArray());
            _inputParamsGrid.Columns.Add(inputTypeColumn);
            _inputParamsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "value", HeaderText = "Value (EntityReference: logical:guid)" });
            AddRow(layout, "InputParameters", Stretch(_inputParamsGrid));

            // ----- Visual Studio (§4.9) -----
            AddSectionHeader(layout, "Visual Studio (optional — manual attach also works)");
            _vsCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            var refreshVsButton = new Button { Text = "Refresh", AutoSize = true };
            refreshVsButton.Click += (s, e) => RefreshVsInstances();
            var attachButton = new Button { Text = "Attach to XrmToolBox", AutoSize = true };
            attachButton.Click += OnAttachVs;
            AddRow(layout, "Debugger", BuildStretchRow(_vsCombo, refreshVsButton, attachButton));

            // Trigger + symbols
            var triggerPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            _triggerButton = new Button { Text = "▶ Trigger", AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Height = 32 };
            _triggerButton.Click += OnTrigger;
            _symbolsLabel = new Label { Text = "symbols: —", AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
            triggerPanel.Controls.Add(_triggerButton);
            triggerPanel.Controls.Add(_symbolsLabel);
            AddRow(layout, "", triggerPanel);

            return layout;
        }

        private Control BuildLogToolbar()
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.FromArgb(45, 45, 45) };
            var copyButton = new Button { Text = "Copy", AutoSize = true, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            copyButton.Click += (s, e) => { if (_logBox.TextLength > 0) Clipboard.SetText(_logBox.Text); };
            var clearButton = new Button { Text = "Clear", AutoSize = true, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            clearButton.Click += (s, e) => _logBox.Clear();
            var saveButton = new Button { Text = "Export…", AutoSize = true, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            saveButton.Click += OnExportLog;
            panel.Controls.Add(copyButton);
            panel.Controls.Add(clearButton);
            panel.Controls.Add(saveButton);
            return panel;
        }

        private static void AddRow(TableLayoutPanel layout, string label, Control control)
        {
            AddRow(layout, label, control, out _);
        }

        private static void AddSectionHeader(TableLayoutPanel layout, string text)
        {
            AddSectionHeader(layout, text, out _, out _);
        }

        private static void AddSectionHeader(TableLayoutPanel layout, string text, out Label header, out Label filler)
        {
            header = new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 90, 158),
                Margin = new Padding(3, 14, 3, 4)
            };
            // Auto-flow + ColumnSpan don't mix cleanly here, so keep it a normal 2-cell row:
            // header in the label column, an empty filler in the field column.
            filler = new Label { Text = string.Empty, AutoSize = true };
            layout.Controls.Add(header);
            layout.Controls.Add(filler);
        }

        private static void AddRow(TableLayoutPanel layout, string label, Control control, out Label labelControl)
        {
            labelControl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
            layout.Controls.Add(labelControl);
            layout.Controls.Add(control);
        }

        /// <summary>
        /// Anchors a field-column control Left+Right so it grows and shrinks with the panel
        /// width when XrmToolBox is resized, instead of keeping its fixed design-time width.
        /// </summary>
        private static T Stretch<T>(T control) where T : Control
        {
            control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            return control;
        }

        /// <summary>
        /// Lays out a primary input that fills the available width followed by trailing controls
        /// (buttons / a name label) that keep their natural size. The whole row anchors Left+Right
        /// so the primary input adapts to the current panel width (requirement: fit available size).
        /// </summary>
        private static Control BuildStretchRow(Control main, params Control[] trailing)
        {
            var row = new TableLayoutPanel
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1 + trailing.Length,
                RowCount = 1,
                Margin = new Padding(0)
            };
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            main.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            main.Margin = new Padding(0, 1, 3, 1);
            row.Controls.Add(main, 0, 0);

            var column = 1;
            foreach (var control in trailing)
            {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                control.Margin = new Padding(0, 1, 3, 1);
                row.Controls.Add(control, column++, 0);
            }
            return row;
        }

        // ---- form-shape gating (§4.3 matrix, driven by FormShapeEngine) --------------------

        private FormShape CurrentShape()
        {
            var message = EffectiveMessageName();
            var stage = SelectedStage();
            // A custom ("Other") message isn't in the v1 matrix — fall back to a permissive shape
            // instead of throwing, so the form still lets the user build a Target/images.
            return FormShapeEngine.IsSupported(message, stage)
                ? FormShapeEngine.Resolve(message, stage)
                : FormShapeEngine.General(message, stage);
        }

        /// <summary>True when the Message dropdown is on the free-text "Other…" sentinel.</summary>
        private bool IsOtherMessage() =>
            (_messageCombo?.SelectedItem as string) == OtherMessageItem;

        /// <summary>
        /// The MessageName the form will emit: the typed value when "Other…" is selected,
        /// otherwise the picked Create / Update / Delete item.
        /// </summary>
        private string EffectiveMessageName() =>
            IsOtherMessage()
                ? _customMessageBox?.Text?.Trim() ?? string.Empty
                : _messageCombo?.SelectedItem as string;

        private void OnMessageChanged()
        {
            if (_customMessageBox != null)
            {
                _customMessageBox.Visible = IsOtherMessage();
            }
            ApplyFormShape();
        }

        private PluginTypeInfo SelectedType() => _typeCombo?.SelectedItem as PluginTypeInfo;

        private bool IsWorkflowSelected() => SelectedType()?.Kind == PluginTypeKind.WorkflowActivity;

        /// <summary>When the selected type changes, populate workflow args (if any) and reshape the form.</summary>
        private void OnTypeChanged()
        {
            var info = SelectedType();
            if (info != null && info.Kind == PluginTypeKind.WorkflowActivity)
            {
                PopulateWorkflowArgs(info);
            }
            ApplyFormShape();
        }

        private void ApplyFormShape()
        {
            if (_messageCombo?.SelectedItem == null || _stageCombo == null)
            {
                return;
            }

            bool isWorkflow = IsWorkflowSelected();

            // Workflow-activity rows appear only for a code activity...
            SetRowVisible(_workflowHeaderLabel, _workflowHeaderFiller, isWorkflow);
            SetRowVisible(_workflowArgsLabel, _workflowArgsGrid, isWorkflow);
            SetRowVisible(_stageNameLabel, _stageNameBox, isWorkflow);
            SetRowVisible(_workflowModeLabel, _workflowModeCombo, isWorkflow);

            if (isWorkflow)
            {
                // ...and the plugin-only Target / images / OutputParameters editors are hidden (FR-12.1).
                SetRowVisible(_targetGridLabel, _targetEditor, false);
                SetRowVisible(_targetIdLabel, _targetIdBox, false);
                SetRowVisible(_outputIdLabel, _outputIdBox, false);
                SetRowVisible(_imageLabel, _imageEditor, false);
                return;
            }

            var shape = CurrentShape();

            // Target editor: typed attribute editor (Create/Update) vs EntityReference id (Delete).
            bool entityTarget = shape.TargetEditor == TargetEditorKind.EntityAttributes;
            bool referenceTarget = shape.TargetEditor == TargetEditorKind.EntityReference;
            SetRowVisible(_targetGridLabel, _targetEditor, entityTarget);
            SetRowVisible(_targetIdLabel, _targetIdBox, referenceTarget);
            if (entityTarget && _targetGridLabel != null)
            {
                _targetGridLabel.Text = shape.TargetLabel;
            }

            // OutputParameters["id"] only for Create / post-operation.
            SetRowVisible(_outputIdLabel, _outputIdBox, shape.ExposesOutputId);

            // Images — hidden entirely (not merely warned) when the message/stage can't carry any (FR-3.3).
            bool anyImageAllowed = shape.PreImageAllowed || shape.PostImageAllowed;
            SetRowVisible(_imageLabel, _imageEditor, anyImageAllowed);
            _imageEditor.SetAllowed(shape.PreImageAllowed, shape.PostImageAllowed);
        }

        private static void SetRowVisible(Control label, Control control, bool visible)
        {
            if (label != null)
            {
                label.Visible = visible;
            }
            if (control != null)
            {
                control.Visible = visible;
            }
        }

        private int SelectedStage()
        {
            switch (_stageCombo.SelectedIndex)
            {
                case 0: return 10;
                case 2: return 40;
                default: return 20;
            }
        }

        private ExecutionMode SelectedMode()
        {
            switch (_modeCombo.SelectedIndex)
            {
                case 1: return ExecutionMode.ReadRealWriteMock;
                case 2: return ExecutionMode.FullMock;
                default: return ExecutionMode.FullReal;
            }
        }

        // ---- event handlers ----------------------------------------------------------------

        private void OnBrowse(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog { Filter = "Plugin assembly (*.dll)|*.dll", Title = "Select built plugin assembly" })
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    _assemblyPathBox.Text = dialog.FileName;
                    _typeCombo.Items.Clear();
                }
            }
        }

        private void OnLoadTypes(object sender, EventArgs e)
        {
            var path = _assemblyPathBox.Text;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                MessageBox.Show("Select a plugin assembly first.", "PluginXray", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                var types = PluginRunner.ListPluginTypes(path);
                _typeCombo.Items.Clear();
                _typeCombo.Items.AddRange(types.Cast<object>().ToArray());
                if (_typeCombo.Items.Count > 0)
                {
                    _typeCombo.SelectedIndex = 0;
                    AppendLog(LogCategory.Info, $"Found {types.Length} plugin type(s) in {Path.GetFileName(path)}.");
                }
                else
                {
                    AppendLog(LogCategory.Warning, "No IPlugin types found in the selected assembly.");
                }
            }
            catch (Exception ex)
            {
                AppendLog(LogCategory.Error, "Failed to enumerate plugin types: " + ex.Message);
            }
        }

        // Requires a connection: ExecuteMethod opens the connection selection dialog when
        // disconnected and replays the hydrate once connected.
        private void OnHydrate(object sender, EventArgs e) => ExecuteMethod(HydrateFromRecord);

        private async void HydrateFromRecord()
        {
            var entityName = SelectedEntityName();
            if (_metadata == null)
            {
                // Service is present but the metadata cache wasn't built — recover instead of blocking.
                _metadata = new MetadataCache(Service);
                UpdateEditorContext();
            }
            if (string.IsNullOrWhiteSpace(entityName))
            {
                MessageBox.Show("Select a table from the Table dropdown first.", "Hydrate", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Guid recordId;
            try
            {
                using (var picker = new RecordPickerDialog(Service, _metadata, entityName))
                {
                    if (picker.ShowDialog(this) != DialogResult.OK)
                    {
                        return;
                    }
                    recordId = picker.SelectedId;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open the record picker: " + ex.Message, "Hydrate", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var shape = CurrentShape();
            _hydratedPrimaryId = recordId;

            // Delete uses an EntityReference Target — hydration just supplies the record id.
            if (shape.TargetEditor == TargetEditorKind.EntityReference)
            {
                _targetIdBox.Text = recordId.ToString();
                AppendLog(LogCategory.Info, $"Hydrated Delete target: {entityName} {recordId}.");
                return;
            }

            try
            {
                var service = Service;
                var record = await Task.Run(() => service.Retrieve(entityName, recordId, new ColumnSet(true)));
                var candidates = HydrationMapper.FromEntity(record);

                // For Update, default to NONE checked so the user opts in to "changed" attributes (FR-3.4).
                using (var dialog = new HydrateAttributesDialog(candidates, checkAllByDefault: !shape.TargetIsChangedAttributesOnly))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        _targetEditor.SetAttributes(dialog.SelectedAttributes);
                        AppendLog(LogCategory.Info, $"Hydrated {dialog.SelectedAttributes.Count} attribute(s) from {entityName} {recordId}.");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not retrieve the record: " + ex.Message, "Hydrate", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---- full execution-context import (§4.11) -----------------------------------------

        private void OnPasteContext(object sender, EventArgs e)
        {
            string json;
            using (var dialog = new PasteContextDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                json = dialog.Json;
            }

            ImportedContext imported;
            try
            {
                imported = ExecutionContextImporter.Import(json);
            }
            catch (Exception ex)
            {
                // Parse failure is non-destructive — the form is untouched (FR-11.8).
                MessageBox.Show(this, ex.Message, "Import execution context",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // The message must be one the form can shape (CUD only, OD-1).
            if (!FormShapeEngine.SupportedMessages.Contains(imported.MessageName))
            {
                MessageBox.Show(this,
                    $"MessageName '{imported.MessageName}' is not supported (v1 handles Create / Update / Delete). " +
                    "Import aborted; the form is unchanged.",
                    "Import execution context", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Confirm before overwriting existing edits (FR-11.8).
            if (HasFormEdits() &&
                MessageBox.Show(this, "Replace the current form contents with the pasted context?",
                    "Import execution context", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                return;
            }

            ApplyImportedContext(imported);
        }

        private bool HasFormEdits()
        {
            return (_targetEditor != null && _targetEditor.Attributes.Count > 0)
                   || (_imageEditor != null && _imageEditor.Entries.Count > 0)
                   || GridHasRows(_inputParamsGrid)
                   || GridHasRows(_sharedVarsGrid)
                   || !string.IsNullOrWhiteSpace(_targetIdBox?.Text);
        }

        private static bool GridHasRows(DataGridView grid)
        {
            if (grid == null)
            {
                return false;
            }
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (!row.IsNewRow)
                {
                    return true;
                }
            }
            return false;
        }

        private void ApplyImportedContext(ImportedContext ic)
        {
            var warnings = new List<string>(ic.Warnings);

            // 1. Message + stage first — their handlers reshape the form (§4.3).
            _messageCombo.SelectedItem = ic.MessageName;
            _stageCombo.SelectedIndex = StageToIndex(ic.Stage, warnings);

            // 2. Table (primary entity) before the editors, since changing it clears them.
            SetTable(ic.PrimaryEntityName);
            UpdateEditorContext();

            ApplyFormShape();
            var shape = CurrentShape();

            // 3. Target — honour the resolved shape, warn on any mismatch with the payload.
            _hydratedPrimaryId = null;
            _targetEditor.Clear();
            _targetIdBox.Text = string.Empty;
            if (shape.TargetEditor == TargetEditorKind.EntityReference)
            {
                if (ic.TargetReference != null)
                {
                    _targetIdBox.Text = ic.TargetReference.Id.ToString();
                }
                else if (ic.TargetEntity != null)
                {
                    _targetIdBox.Text = ic.TargetEntity.Id.ToString();
                    warnings.Add("Target was an Entity but the message needs an EntityReference — used its id only.");
                }
            }
            else
            {
                if (ic.TargetEntity != null)
                {
                    _targetEditor.SetAttributes(HydrationMapper.FromEntity(ic.TargetEntity));
                    if (ic.TargetEntity.Id != Guid.Empty)
                    {
                        _hydratedPrimaryId = ic.TargetEntity.Id;
                    }
                }
                else if (ic.TargetReference != null)
                {
                    warnings.Add("Target was an EntityReference but the message needs an Entity — no attributes were filled.");
                }
            }

            // 4. OutputParameters["id"] where the shape exposes it.
            if (shape.ExposesOutputId && ic.OutputId.HasValue)
            {
                _outputIdBox.Text = ic.OutputId.Value.ToString();
            }

            // 5. Remaining context fields.
            _contextModeCombo.SelectedIndex = ic.Mode == 1 ? 1 : 0;
            _depthBox.Text = ic.Depth.ToString();
            SetGuidBox(_userIdBox, ic.UserId);
            SetGuidBox(_initiatingUserIdBox, ic.InitiatingUserId);
            SetGuidBox(_businessUnitIdBox, ic.BusinessUnitId);
            SetGuidBox(_organizationIdBox, ic.OrganizationId);
            if (ic.CorrelationId != Guid.Empty)
            {
                _correlationIdBox.Text = ic.CorrelationId.ToString();
            }

            // 6. SharedVariables + arbitrary InputParameters grids.
            FillNamedValueGrid(_sharedVarsGrid, ic.SharedVariables, forInputParameters: false, warnings);
            FillNamedValueGrid(_inputParamsGrid, ic.InputParameters, forInputParameters: true, warnings);

            // 7. Images honoured by the resolved shape's gating (§4.3).
            ApplyImportedImages(ic, shape, warnings);

            ReportImport(ic, warnings);
        }

        /// <summary>Selects the primary-entity table, matching a known item where possible.</summary>
        private void SetTable(string logicalName)
        {
            if (_tableCombo == null || string.IsNullOrEmpty(logicalName))
            {
                return;
            }

            foreach (var obj in _tableCombo.Items)
            {
                if (obj is TableItem item && string.Equals(item.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase))
                {
                    _tableCombo.SelectedItem = item;
                    return;
                }
            }

            _tableCombo.SelectedIndex = -1;
            _tableCombo.Text = logicalName;
        }

        private int StageToIndex(int stage, List<string> warnings)
        {
            switch (stage)
            {
                case 10: return 0;
                case 20: return 1;
                case 40: return 2;
                default:
                    warnings.Add($"Stage {stage} is not modeled (only 10 / 20 / 40) — used 20 (pre-operation).");
                    return 1;
            }
        }

        private void ApplyImportedImages(ImportedContext ic, FormShape shape, List<string> warnings)
        {
            var entries = new List<ImageEntry>();
            foreach (var image in ic.Images)
            {
                if ((image.IsPreImage && !shape.PreImageAllowed) || (!image.IsPreImage && !shape.PostImageAllowed))
                {
                    warnings.Add($"{(image.IsPreImage ? "Pre" : "Post")}Image '{image.Key}' is not allowed at " +
                                 $"{shape.Message}/{shape.Stage} and was skipped.");
                    continue;
                }

                entries.Add(new ImageEntry
                {
                    IsPreImage = image.IsPreImage,
                    Key = image.Key,
                    Attributes = HydrationMapper.FromEntity(image.Entity)
                });
            }
            _imageEditor.SetEntries(entries);
        }

        /// <summary>Populates a Key/Type/Value grid from imported named values, warning on any that don't fit.</summary>
        private void FillNamedValueGrid(DataGridView grid, List<ImportedNamedValue> values, bool forInputParameters, List<string> warnings)
        {
            grid.Rows.Clear();
            foreach (var nv in values)
            {
                if (nv.Value == null)
                {
                    warnings.Add($"{(forInputParameters ? "InputParameter" : "SharedVariable")} '{nv.Key}' had a null/unsupported value and was skipped.");
                    continue;
                }

                if (!TryDescribeValue(nv.Value, forInputParameters, out var typeName, out var text))
                {
                    warnings.Add($"{(forInputParameters ? "InputParameter" : "SharedVariable")} '{nv.Key}' " +
                                 $"({nv.Value.GetType().Name}) is not representable in the grid and was skipped.");
                    continue;
                }

                grid.Rows.Add(nv.Key, typeName, text);
            }
        }

        /// <summary>Maps a boxed value to the (Type, Value) cell text for a Key/Type/Value grid.</summary>
        private static bool TryDescribeValue(object value, bool forInputParameters, out string typeName, out string text)
        {
            switch (value)
            {
                case string s: typeName = "String"; text = s; return true;
                case bool b: typeName = "Boolean"; text = b ? "true" : "false"; return true;
                case int i: typeName = "WholeNumber"; text = i.ToString(CultureInfo.InvariantCulture); return true;
                case long l: typeName = "WholeNumber"; text = l.ToString(CultureInfo.InvariantCulture); return true;
                case decimal d: typeName = "Decimal"; text = d.ToString(CultureInfo.InvariantCulture); return true;
                case double db: typeName = "Double"; text = db.ToString(CultureInfo.InvariantCulture); return true;
                case DateTime dt: typeName = "DateTime"; text = dt.ToString("o", CultureInfo.InvariantCulture); return true;
                case Guid g: typeName = "Guid"; text = g.ToString(); return true;
            }

            // SDK types only fit the richer InputParameters grid (FR-4.6).
            if (forInputParameters)
            {
                switch (value)
                {
                    case Money m: typeName = "Money"; text = m.Value.ToString(CultureInfo.InvariantCulture); return true;
                    case OptionSetValue osv: typeName = "OptionSetValue"; text = osv.Value.ToString(CultureInfo.InvariantCulture); return true;
                    case EntityReference er: typeName = "EntityReference"; text = $"{er.LogicalName}:{er.Id}"; return true;
                }
            }

            typeName = null;
            text = null;
            return false;
        }

        private static void SetGuidBox(TextBox box, Guid value)
        {
            if (box != null && value != Guid.Empty)
            {
                box.Text = value.ToString();
            }
        }

        private void ReportImport(ImportedContext ic, List<string> warnings)
        {
            AppendLog(LogCategory.Info,
                $"Imported context: {ic.MessageName} / stage {ic.Stage} / {ic.PrimaryEntityName} " +
                $"(target={ic.TargetKind}, inputParams={ic.InputParameters.Count}, images={ic.Images.Count}, " +
                $"sharedVars={ic.SharedVariables.Count}).");

            foreach (var warning in warnings)
            {
                AppendLog(LogCategory.Warning, "Import: " + warning);
            }

            if (warnings.Count > 0)
            {
                MessageBox.Show(this,
                    $"Context imported with {warnings.Count} note(s). See the run log for details — review the form before triggering.",
                    "Import execution context", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void RefreshVsInstances()
        {
            try
            {
                var instances = VisualStudioAttacher.GetRunningInstances();
                _vsCombo.Items.Clear();
                _vsCombo.Items.AddRange(instances.Cast<object>().ToArray());
                if (_vsCombo.Items.Count > 0)
                {
                    _vsCombo.SelectedIndex = 0;
                    AppendLog(LogCategory.Info, $"Found {instances.Count} running Visual Studio instance(s).");
                }
                else
                {
                    AppendLog(LogCategory.Warning, "No running Visual Studio instances found (you can still attach manually).");
                }
            }
            catch (Exception ex)
            {
                AppendLog(LogCategory.Error, "Could not enumerate Visual Studio instances: " + ex.Message);
            }
        }

        private void OnAttachVs(object sender, EventArgs e)
        {
            if (!(_vsCombo.SelectedItem is VsInstance instance))
            {
                MessageBox.Show("Refresh and select a Visual Studio instance first.", "Attach debugger",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var hostPid = Process.GetCurrentProcess().Id;
            try
            {
                VisualStudioAttacher.Attach(instance, hostPid);
                AppendLog(LogCategory.Info, $"Attached {instance} to the XrmToolBox host process (pid {hostPid}). Set your breakpoints, then Trigger.");
            }
            catch (Exception ex)
            {
                AppendLog(LogCategory.Error, "Attach failed: " + ex.Message);
                MessageBox.Show(this,
                    "Could not attach automatically: " + ex.Message +
                    "\n\nYou can attach manually in Visual Studio: Debug → Attach to Process → select the XrmToolBox process.",
                    "Attach debugger", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Requires a connection: ExecuteMethod opens the connection selection dialog when
        // disconnected and replays the run once connected.
        private void OnTrigger(object sender, EventArgs e) => ExecuteMethod(RunPlugin);

        private async void RunPlugin()
        {
            if (!ValidateForRun(out var dllPath, out var typeInfo))
            {
                return;
            }

            var mode = SelectedMode();
            if (mode == ExecutionMode.FullReal && !ConfirmRealWrites())
            {
                return;
            }

            RunRequest request;
            try
            {
                request = BuildRunRequest(typeInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "PluginXray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _triggerButton.Enabled = false;
            _symbolsLabel.Text = "symbols: running…";
            AppendLog(LogCategory.Info, "──────────────────────────────────────────");

            try
            {
                var service = Service;
                var outcome = await Task.Run(() => PluginRunner.Run(dllPath, request, service, _logSink));
                ShowOutcome(outcome);
            }
            catch (Exception ex)
            {
                AppendLog(LogCategory.Error, "Run failed: " + ex);
            }
            finally
            {
                _triggerButton.Enabled = true;
            }
        }

        private bool ValidateForRun(out string dllPath, out PluginTypeInfo typeInfo)
        {
            dllPath = _assemblyPathBox.Text;
            typeInfo = _typeCombo.SelectedItem as PluginTypeInfo;

            // Reached only via ExecuteMethod(RunPlugin), so a connection is already established.
            if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
            {
                MessageBox.Show("Select a valid plugin assembly.", "PluginXray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (typeInfo == null)
            {
                MessageBox.Show("Load and select a plugin type.", "PluginXray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (IsOtherMessage() && string.IsNullOrWhiteSpace(EffectiveMessageName()))
            {
                MessageBox.Show("Type a message name for the 'Other…' option.", "PluginXray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // A workflow activity has no Target, so a table (primary entity) is optional; instead
            // check that every required input argument has a value (FR-12.4).
            if (typeInfo.Kind == PluginTypeKind.WorkflowActivity)
            {
                var missing = MissingRequiredArguments();
                if (missing.Count > 0)
                {
                    MessageBox.Show("Supply a value for the required argument(s): " + string.Join(", ", missing) + ".",
                        "PluginXray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                return true;
            }

            // A custom ("Other") message — e.g. an unbound Custom API — has no primary entity, so
            // the table is optional. Create / Update / Delete still require one.
            if (!IsOtherMessage() && string.IsNullOrWhiteSpace(SelectedEntityName()))
            {
                MessageBox.Show("Select a table (primary entity).", "PluginXray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        private bool ConfirmRealWrites()
        {
            var env = ConnectionDetail?.ConnectionName ?? "(unknown)";
            var result = MessageBox.Show(
                $"Full-real mode will execute Create/Update/Delete requests against:\n\n    {env}\n\n" +
                "There is no platform transaction — partial writes are NOT rolled back on failure.\n\nProceed?",
                "Confirm real writes",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            return result == DialogResult.Yes;
        }

        // ---- custom workflow activity (§4.12) ----------------------------------------------

        /// <summary>Fills the arguments grid from the reflected In/Out arguments of the selected activity.</summary>
        private void PopulateWorkflowArgs(PluginTypeInfo info)
        {
            _workflowArgsGrid.Rows.Clear();
            foreach (var arg in info.Arguments)
            {
                var display = arg.DisplayName + (arg.Required ? " *" : string.Empty);
                var index = _workflowArgsGrid.Rows.Add(display, arg.Direction, arg.TypeName, string.Empty);
                var row = _workflowArgsGrid.Rows[index];
                row.Tag = arg;

                if (!arg.IsInput)
                {
                    // Output-only arguments are read back after the run, not supplied.
                    row.Cells[3].Value = "(output)";
                    row.Cells[3].ReadOnly = true;
                    row.DefaultCellStyle.ForeColor = Color.Gray;
                }
            }
        }

        private List<string> MissingRequiredArguments()
        {
            var missing = new List<string>();
            foreach (DataGridViewRow row in _workflowArgsGrid.Rows)
            {
                if (row.Tag is WorkflowArgumentInfo arg && arg.Required && arg.IsInput
                    && string.IsNullOrWhiteSpace(row.Cells[3].Value?.ToString()))
                {
                    missing.Add(arg.Name);
                }
            }
            return missing;
        }

        private List<WorkflowArgumentDto> BuildWorkflowArguments()
        {
            var list = new List<WorkflowArgumentDto>();
            foreach (DataGridViewRow row in _workflowArgsGrid.Rows)
            {
                if (!(row.Tag is WorkflowArgumentInfo arg) || !arg.IsInput)
                {
                    continue;
                }

                var text = row.Cells[3].Value?.ToString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue; // optional argument left blank
                }

                if (!TryResolveArgumentType(arg.TypeName, out var type))
                {
                    throw new FormatException(
                        $"Argument '{arg.Name}' has an unsupported type '{arg.TypeName}'. Supported: string, int, bool, " +
                        "decimal, double, DateTime, Guid, Money, OptionSetValue, EntityReference.");
                }

                object boxed;
                try
                {
                    boxed = InputParameterValue.Parse(type, text);
                }
                catch (Exception ex)
                {
                    throw new FormatException($"Argument '{arg.Name}' ({arg.TypeName}): {ex.Message}");
                }

                list.Add(new WorkflowArgumentDto
                {
                    Name = arg.Name,
                    ValueXml = SdkXml.Serialize(boxed, typeof(object)),
                    ValueType = type.ToString()
                });
            }
            return list;
        }

        /// <summary>Maps a reflected argument value-type name (T of InArgument&lt;T&gt;) to a typed input.</summary>
        private static bool TryResolveArgumentType(string clrTypeName, out InputParameterType type)
        {
            switch (clrTypeName)
            {
                case "String": type = InputParameterType.String; return true;
                case "Int32": type = InputParameterType.WholeNumber; return true;
                case "Boolean": type = InputParameterType.Boolean; return true;
                case "Decimal": type = InputParameterType.Decimal; return true;
                case "Double": type = InputParameterType.Double; return true;
                case "DateTime": type = InputParameterType.DateTime; return true;
                case "Guid": type = InputParameterType.Guid; return true;
                case "Money": type = InputParameterType.Money; return true;
                case "OptionSetValue": type = InputParameterType.OptionSetValue; return true;
                case "EntityReference": type = InputParameterType.EntityReference; return true;
                default: type = default(InputParameterType); return false;
            }
        }

        private RunRequest BuildWorkflowRunRequest(PluginTypeInfo typeInfo)
        {
            var context = new ContextDto
            {
                MessageName = EffectiveMessageName(),
                PrimaryEntityName = SelectedEntityName(), // optional for a workflow activity
                Mode = _contextModeCombo.SelectedIndex < 0 ? 0 : _contextModeCombo.SelectedIndex,
                Depth = ParseIntOr(_depthBox.Text, 1),
                UserId = ParseGuidOrEmpty(_userIdBox.Text),
                InitiatingUserId = ParseGuidOrEmpty(_initiatingUserIdBox.Text),
                BusinessUnitId = ParseGuidOrEmpty(_businessUnitIdBox.Text),
                OrganizationId = ParseGuidOrEmpty(_organizationIdBox.Text),
                OrganizationName = ConnectionDetail?.ConnectionName,
                CorrelationId = Guid.TryParse(_correlationIdBox.Text.Trim(), out var corr) ? corr : Guid.NewGuid(),
                StageName = _stageNameBox.Text?.Trim(),
                WorkflowMode = _workflowModeCombo.SelectedIndex < 0 ? 0 : _workflowModeCombo.SelectedIndex
            };
            context.SharedVariables.AddRange(BuildSharedVariables());
            if (_hydratedPrimaryId.HasValue)
            {
                context.PrimaryEntityId = _hydratedPrimaryId.Value;
            }

            return new RunRequest
            {
                PluginTypeName = typeInfo.FullName,
                Kind = PluginTypeKind.WorkflowActivity,
                Mode = SelectedMode(),
                Context = context,
                InputArguments = BuildWorkflowArguments()
            };
        }

        private RunRequest BuildRunRequest(PluginTypeInfo typeInfo)
        {
            if (typeInfo.Kind == PluginTypeKind.WorkflowActivity)
            {
                return BuildWorkflowRunRequest(typeInfo);
            }

            var shape = CurrentShape();
            var entityName = SelectedEntityName();

            var context = new ContextDto
            {
                MessageName = shape.Message,
                PrimaryEntityName = entityName,
                Stage = shape.Stage,
                Mode = _contextModeCombo.SelectedIndex < 0 ? 0 : _contextModeCombo.SelectedIndex,
                Depth = ParseIntOr(_depthBox.Text, 1),
                UserId = ParseGuidOrEmpty(_userIdBox.Text),
                InitiatingUserId = ParseGuidOrEmpty(_initiatingUserIdBox.Text),
                BusinessUnitId = ParseGuidOrEmpty(_businessUnitIdBox.Text),
                OrganizationId = ParseGuidOrEmpty(_organizationIdBox.Text),
                OrganizationName = ConnectionDetail?.ConnectionName,
                CorrelationId = Guid.TryParse(_correlationIdBox.Text.Trim(), out var corr) ? corr : Guid.NewGuid()
            };
            context.SharedVariables.AddRange(BuildSharedVariables());
            context.InputParameters.AddRange(BuildInputParameters());

            if (shape.TargetEditor == TargetEditorKind.EntityReference)
            {
                Guid.TryParse(_targetIdBox.Text.Trim(), out var id);
                context.PrimaryEntityId = id;
                context.TargetKind = TargetKind.EntityReference;
                context.TargetXml = SdkXml.Serialize(new EntityReference(entityName, id), typeof(EntityReference));
            }
            // A custom ("Other") message with no table and no attributes — e.g. an unbound Custom
            // API — carries no Target at all; leave TargetKind.None so no "Target" is injected.
            else if (!string.IsNullOrWhiteSpace(entityName) || _targetEditor.Attributes.Count > 0)
            {
                var target = TypedAttribute.ToEntity(entityName, _targetEditor.Attributes);

                // For Update against a hydrated record, the Target carries the record id.
                if (shape.Message == "Update" && _hydratedPrimaryId.HasValue)
                {
                    target.Id = _hydratedPrimaryId.Value;
                    context.PrimaryEntityId = _hydratedPrimaryId.Value;
                }

                context.TargetKind = TargetKind.Entity;
                context.TargetXml = SdkXml.Serialize(target, typeof(Entity));

                if (shape.ExposesOutputId)
                {
                    context.OutputId = Guid.TryParse(_outputIdBox.Text.Trim(), out var outId) ? outId : Guid.NewGuid();
                }
            }

            // Images: attach each user-defined image the resolved shape permits. An image needs a
            // table to build its Entity, so skip them when none is selected (unbound custom message).
            foreach (var image in string.IsNullOrWhiteSpace(entityName)
                ? Enumerable.Empty<ImageEntry>()
                : _imageEditor.Entries)
            {
                if ((image.IsPreImage && !shape.PreImageAllowed) || (!image.IsPreImage && !shape.PostImageAllowed))
                {
                    continue;
                }

                var dto = new ImageDto
                {
                    Key = image.Key,
                    EntityXml = SdkXml.Serialize(TypedAttribute.ToEntity(entityName, image.Attributes), typeof(Entity))
                };
                (image.IsPreImage ? context.PreImages : context.PostImages).Add(dto);
            }

            return new RunRequest
            {
                PluginTypeName = typeInfo.FullName,
                UnsecureConfig = _unsecureBox.Text,
                SecureConfig = _secureBox.Text,
                Mode = SelectedMode(),
                Context = context
            };
        }

        private List<SharedVariableDto> BuildSharedVariables()
        {
            var list = new List<SharedVariableDto>();
            foreach (DataGridViewRow row in _sharedVarsGrid.Rows)
            {
                if (row.IsNewRow)
                {
                    continue;
                }

                var key = row.Cells[0].Value?.ToString();
                var typeName = row.Cells[1].Value?.ToString();
                var text = row.Cells[2].Value?.ToString();

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(typeName))
                {
                    continue;
                }
                if (!SharedVariableValue.TryParseType(typeName, out var type))
                {
                    continue;
                }

                object boxed;
                try
                {
                    boxed = SharedVariableValue.Parse(type, text);
                }
                catch (Exception ex)
                {
                    throw new FormatException($"SharedVariable '{key}' ({typeName}): {ex.Message}");
                }

                list.Add(new SharedVariableDto
                {
                    Key = key.Trim(),
                    ValueXml = SdkXml.Serialize(boxed, typeof(object)),
                    ValueType = typeName
                });
            }
            return list;
        }

        private List<InputParameterDto> BuildInputParameters()
        {
            var list = new List<InputParameterDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DataGridViewRow row in _inputParamsGrid.Rows)
            {
                if (row.IsNewRow)
                {
                    continue;
                }

                var key = row.Cells[0].Value?.ToString();
                var typeName = row.Cells[1].Value?.ToString();
                var text = row.Cells[2].Value?.ToString();

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(typeName))
                {
                    continue;
                }

                key = key.Trim();

                // The Target slot is owned by the message/stage form-shape engine (FR-4.6).
                if (string.Equals(key, "Target", StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatException(
                        "InputParameters: 'Target' is managed by the Message/Stage form and cannot be added here — " +
                        "use the Target editor above.");
                }
                if (!seen.Add(key))
                {
                    throw new FormatException($"InputParameters: duplicate parameter name '{key}'.");
                }
                if (!InputParameterValue.TryParseType(typeName, out var type))
                {
                    continue;
                }

                object boxed;
                try
                {
                    boxed = InputParameterValue.Parse(type, text);
                }
                catch (Exception ex)
                {
                    throw new FormatException($"InputParameter '{key}' ({typeName}): {ex.Message}");
                }

                list.Add(new InputParameterDto
                {
                    Key = key,
                    ValueXml = SdkXml.Serialize(boxed, typeof(object)),
                    ValueType = typeName
                });
            }
            return list;
        }

        private static int ParseIntOr(string text, int fallback) =>
            int.TryParse((text ?? string.Empty).Trim(), out var value) ? value : fallback;

        private static Guid ParseGuidOrEmpty(string text) =>
            Guid.TryParse((text ?? string.Empty).Trim(), out var value) ? value : Guid.Empty;

        private void ShowOutcome(RunOutcome outcome)
        {
            _symbolsLabel.Text = outcome.SymbolsLoaded ? "symbols: loaded ✓" : "symbols: NOT loaded ✗ (no .pdb)";
            _symbolsLabel.ForeColor = outcome.SymbolsLoaded ? Color.Green : Color.Firebrick;

            var result = outcome.Result;
            if (result.Success)
            {
                AppendLog(LogCategory.Info, "✔ Run succeeded.");
                foreach (var op in result.OutputParameters)
                {
                    AppendLog(LogCategory.Info, $"    OutputParameters[\"{op.Key}\"] = {op.Display}");
                }
            }
            else
            {
                AppendLog(LogCategory.Error, "✘ Run failed — see exception above.");
            }
        }

        private void OnExportLog(object sender, EventArgs e)
        {
            using (var dialog = new SaveFileDialog { Filter = "Text file (*.txt)|*.txt", FileName = "plugin-debug-run.txt" })
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    File.WriteAllText(dialog.FileName, _logBox.Text);
                }
            }
        }

        // ---- logging -----------------------------------------------------------------------

        private void OnEntryLogged(object sender, LogEntry entry)
        {
            // Raised on a worker thread; marshal to the UI thread.
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => AppendLog(entry.Category, entry.Message, entry.Timestamp)));
            }
            else
            {
                AppendLog(entry.Category, entry.Message, entry.Timestamp);
            }
        }

        private void AppendLog(LogCategory category, string message)
        {
            AppendLog(category, message, DateTime.Now);
        }

        private void AppendLog(LogCategory category, string message, DateTime timestamp)
        {
            if (_logBox == null)
            {
                return;
            }

            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionColor = ColorFor(category);
            _logBox.AppendText($"[{timestamp:HH:mm:ss}] {CategoryTag(category)} {message}{Environment.NewLine}");
            _logBox.SelectionColor = _logBox.ForeColor;
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }

        private static string CategoryTag(LogCategory category)
        {
            switch (category)
            {
                case LogCategory.Trace: return "TRACE ";
                case LogCategory.SdkReal: return "SDK→  ";
                case LogCategory.SdkMock: return "SDK✗  ";
                case LogCategory.Warning: return "WARN  ";
                case LogCategory.Error: return "ERROR ";
                default: return "INFO  ";
            }
        }

        private static Color ColorFor(LogCategory category)
        {
            switch (category)
            {
                case LogCategory.Trace: return Color.SkyBlue;
                case LogCategory.SdkReal: return Color.LightGreen;
                case LogCategory.SdkMock: return Color.Khaki;
                case LogCategory.Warning: return Color.Orange;
                case LogCategory.Error: return Color.Salmon;
                default: return Color.Gainsboro;
            }
        }
    }
}
