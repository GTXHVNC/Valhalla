using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Valhalla
{
    public partial class Form1
    {
        private NotifyIcon notificationsNotifyIcon;
        private Icon       notificationsNotifyIconImage;
        private Panel      notificationsPageRoot;
        private CheckBox   notificationsWindowsToggle;
        private Label      notificationsWindowsStatusLabel;
        private TextBox    notificationsTelegramTokenTextBox;
        private TextBox    notificationsTelegramChatIdTextBox;
        private CheckBox   notificationsTelegramToggle;
        private Label      notificationsTelegramStatusLabel;
        private Button     notificationsTelegramTestButton;
        private bool       notificationsUiInitializing;
        private NotificationSettings notificationSettings;

        // ── Palette ───────────────────────────────────────────────────────────
        private static readonly Color NB  = Color.FromArgb(30, 30, 30);
        private static readonly Color NC  = Color.FromArgb(40, 40, 40);
        private static readonly Color NS  = Color.FromArgb(50, 50, 50);
        private static readonly Color NF  = Color.FromArgb(58, 58, 58);
        private static readonly Color NBo = Color.FromArgb(68, 68, 68);
        private static readonly Color NM  = Color.FromArgb(160, 160, 160);
        private static readonly Color NW  = Color.White;
        private static readonly Color NA  = UiTheme.AccentColor;

        // ─────────────────────────────────────────────────────────────────────

        private void InitializeNotificationsPage()
        {
            notificationSettings = NotificationSettingsStore.Load();
            BuildNotificationsPage();
            InitializeNotificationsNotifyIcon();
            ApplyNotificationSettingsToUi();
        }

        private void BuildNotificationsPage()
        {
            notificationsTabPage.SuspendLayout();
            notificationsTabPage.Controls.Clear();
            notificationsTabPage.BackColor = NB;
            notificationsTabPage.Padding   = new Padding(0);

            // ── Root scroll container — fills the entire tab ──────────────────
            // No manual Padding here; we use an inner margin panel instead so
            // ClientSize always equals the full available area and no arithmetic
            // is needed to derive child widths. This avoids the DPI double-
            // subtraction bug that occurs when Padding is set on an AutoScroll
            // panel and subtracted again in the Resize handler.
            notificationsPageRoot = new Panel
            {
                Name       = "notificationsPageRoot",
                Dock       = DockStyle.Fill,
                BackColor  = NB,
                AutoScroll = true,
                Padding    = new Padding(0)
            };

            // ── Outer margin wrapper — provides the visual 24 px side gutters
            //    without touching notificationsPageRoot.Padding, keeping
            //    ClientSize trustworthy for all child-width calculations.
            var marginPanel = new Panel
            {
                Dock      = DockStyle.Top,
                BackColor = NB,
                Padding   = new Padding(24, 20, 24, 0)
            };

            // ── Inner column — always fills marginPanel's client area exactly.
            //    DockStyle.Fill is the only reliable way to track parent width
            //    across DPI changes, window resizes and PerMonitorV2 rescaling.
            var innerColumn = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = NB,
                Padding   = new Padding(0)
            };

            // ── Stack of sections inside innerColumn ─────────────────────────
            // We use a FlowLayoutPanel so adding future sections is trivial.
            var flow = new FlowLayoutPanel
            {
                Dock          = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents  = false,
                AutoSize      = false,   // must be false when Dock=Fill
                BackColor     = NB,
                Padding       = new Padding(0)
            };

            // ── Build section controls ────────────────────────────────────────
            var header       = BuildHeader();
            var divider      = new Panel { BackColor = NBo, Height = 1 };
            var spacer0      = new Panel { BackColor = NB,  Height = 14 };
            var windowsCard  = BuildWindowsSection();
            var sectionGap   = new Panel { BackColor = NB,  Height = 14 };
            var telegramCard = BuildTelegramSection();
            var bottomPad    = new Panel { BackColor = NB,  Height = 28 };

            // Wire each child so it tracks flow's width.
            // Flow itself tracks innerColumn which tracks marginPanel which
            // tracks notificationsPageRoot (Dock=Fill) — one clean chain.
            void TrackWidth(Control child)
            {
                // Set immediately with current width, then keep in sync.
                child.Width = flow.ClientSize.Width;
                flow.ClientSizeChanged += (s, e) => child.Width = flow.ClientSize.Width;
            }

            TrackWidth(header);
            TrackWidth(divider);
            TrackWidth(spacer0);
            TrackWidth(windowsCard);
            TrackWidth(sectionGap);
            TrackWidth(telegramCard);
            TrackWidth(bottomPad);

            flow.Controls.Add(header);
            flow.Controls.Add(divider);
            flow.Controls.Add(spacer0);
            flow.Controls.Add(windowsCard);
            flow.Controls.Add(sectionGap);
            flow.Controls.Add(telegramCard);
            flow.Controls.Add(bottomPad);

            innerColumn.Controls.Add(flow);
            marginPanel.Controls.Add(innerColumn);

            // marginPanel.Height must track its content so the AutoScroll on
            // notificationsPageRoot works correctly.
            flow.Layout += (s, e) =>
            {
                int contentH = 0;
                foreach (Control c in flow.Controls)
                    contentH += c.Height + c.Margin.Vertical;
                marginPanel.Height = contentH
                                   + marginPanel.Padding.Vertical
                                   + 28;   // bottom breathing room
            };

            notificationsPageRoot.Controls.Add(marginPanel);

            notificationsTabPage.Controls.Add(notificationsPageRoot);
            notificationsTabPage.ResumeLayout(true);
        }

        // ════════════════════════════════════════════════════════════════════════
        // HEADER
        // ════════════════════════════════════════════════════════════════════════
        private Panel BuildHeader()
        {
            var header = new Panel { BackColor = NB, Height = 104 };

            var titleLabel = new Label
            {
                AutoSize  = true,
                Location  = new Point(17, 23),
                Font      = new Font("Tahoma", 9.75F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = NW,
                Text      = "Notifications"
            };

            var subtitleLabel = new Label
            {
                AutoSize  = true,
                Location  = new Point(18, 62),
                Font      = new Font("Tahoma", 9.75F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(224, 224, 224),
                Text      = "Windows and Telegram"
            };

            header.Controls.Add(titleLabel);
            header.Controls.Add(subtitleLabel);
            return header;
        }

        // ════════════════════════════════════════════════════════════════════════
        // WINDOWS SECTION
        // ════════════════════════════════════════════════════════════════════════
        private Panel BuildWindowsSection()
        {
            var card = CreateSectionCard();

            var sectionTitle = new Label
            {
                AutoSize  = true,
                Location  = new Point(20, 20),
                Font      = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = NW,
                Text      = "Windows Notifications"
            };

            var sectionDesc = new Label
            {
                AutoSize  = false,
                Height    = 20,
                Location  = new Point(20, 46),
                Font      = new Font("Segoe UI", 8.75F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = NM,
                Text      = "Show a Windows toast when a new client connects"
            };

            var innerDivider = new Panel { BackColor = NBo, Height = 1, Location = new Point(0, 78) };

            var toggleArea = new Panel { BackColor = NS, Height = 56, Location = new Point(0, 79) };

            var togglePrompt = new Label
            {
                AutoSize  = true,
                Location  = new Point(20, 18),
                Font      = new Font("Segoe UI", 9.25F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = NW,
                Text      = "Enable Windows alerts"
            };

            notificationsWindowsToggle = CreatePillToggle();
            notificationsWindowsToggle.Name           = "notificationsWindowsToggle";
            notificationsWindowsToggle.CheckedChanged += notificationsWindowsToggle_CheckedChanged;

            notificationsWindowsStatusLabel = new Label
            {
                Name      = "notificationsWindowsStatusLabel",
                AutoSize  = true,
                Font      = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = NM,
                Text      = "Disabled"
            };

            toggleArea.Resize += (s, e) =>
            {
                notificationsWindowsToggle.Location = new Point(
                    toggleArea.Width - notificationsWindowsToggle.Width - 20, 14);
                notificationsWindowsStatusLabel.Location = new Point(
                    notificationsWindowsToggle.Left - notificationsWindowsStatusLabel.Width - 10, 18);
            };

            toggleArea.Controls.Add(togglePrompt);
            toggleArea.Controls.Add(notificationsWindowsStatusLabel);
            toggleArea.Controls.Add(notificationsWindowsToggle);

            // Sync child widths when the card resizes (driven by TrackWidth above).
            card.Resize += (s, e) =>
            {
                innerDivider.Width = card.ClientSize.Width;
                toggleArea.Width   = card.ClientSize.Width;
                sectionDesc.Width  = card.ClientSize.Width - 40;
            };

            card.Controls.Add(sectionTitle);
            card.Controls.Add(sectionDesc);
            card.Controls.Add(innerDivider);
            card.Controls.Add(toggleArea);

            card.Height = 135;
            return card;
        }

        // ════════════════════════════════════════════════════════════════════════
        // TELEGRAM SECTION
        // ════════════════════════════════════════════════════════════════════════
        private Panel BuildTelegramSection()
        {
            var card = CreateSectionCard();

            var sectionTitle = new Label
            {
                AutoSize  = true,
                Location  = new Point(20, 20),
                Font      = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = NW,
                Text      = "Telegram Notifications"
            };

            var sectionDesc = new Label
            {
                AutoSize  = false,
                Height    = 20,
                Location  = new Point(20, 46),
                Font      = new Font("Segoe UI", 8.75F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = NM,
                Text      = "Forward connection alerts to a Telegram bot chat"
            };

            var innerDivider = new Panel { BackColor = NBo, Height = 1, Location = new Point(0, 78) };

            var fieldsArea = new Panel { BackColor = NS, Height = 152, Location = new Point(0, 79) };

            var tokenLabel = new Label
            {
                AutoSize  = true,
                Location  = new Point(20, 16),
                Font      = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = NW,
                Text      = "Bot Token"
            };

            notificationsTelegramTokenTextBox = CreateStyledTextBox(true);
            notificationsTelegramTokenTextBox.Name         = "notificationsTelegramTokenTextBox";
            notificationsTelegramTokenTextBox.Location     = new Point(20, 34);
            notificationsTelegramTokenTextBox.TextChanged += notificationsTelegramSettingsChanged;

            var chatLabel = new Label
            {
                AutoSize  = true,
                Location  = new Point(20, 78),
                Font      = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = NW,
                Text      = "Chat ID"
            };

            notificationsTelegramChatIdTextBox = CreateStyledTextBox(false);
            notificationsTelegramChatIdTextBox.Name         = "notificationsTelegramChatIdTextBox";
            notificationsTelegramChatIdTextBox.Location     = new Point(20, 96);
            notificationsTelegramChatIdTextBox.TextChanged  += notificationsTelegramSettingsChanged;

            fieldsArea.Resize += (s, e) =>
            {
                int fw = fieldsArea.ClientSize.Width - 40;
                notificationsTelegramTokenTextBox.Width  = fw;
                notificationsTelegramChatIdTextBox.Width = fw;
            };

            fieldsArea.Controls.Add(tokenLabel);
            fieldsArea.Controls.Add(notificationsTelegramTokenTextBox);
            fieldsArea.Controls.Add(chatLabel);
            fieldsArea.Controls.Add(notificationsTelegramChatIdTextBox);

            var actionDivider = new Panel { BackColor = NBo, Height = 1, Location = new Point(0, 231) };

            var actionRow = new Panel { BackColor = NC, Height = 60, Location = new Point(0, 232) };

            var togglePrompt = new Label
            {
                AutoSize  = true,
                Location  = new Point(20, 20),
                Font      = new Font("Segoe UI", 9.25F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = NW,
                Text      = "Enable Telegram alerts"
            };

            notificationsTelegramToggle = CreatePillToggle();
            notificationsTelegramToggle.Name           = "notificationsTelegramToggle";
            notificationsTelegramToggle.CheckedChanged += notificationsTelegramToggle_CheckedChanged;

            notificationsTelegramStatusLabel = new Label
            {
                Name      = "notificationsTelegramStatusLabel",
                AutoSize  = true,
                Font      = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = NM,
                Text      = "Setup required"
            };

            notificationsTelegramTestButton = new Button
            {
                Name      = "notificationsTelegramTestButton",
                Text      = "Send Test",
                AutoSize  = false,
                Size      = new Size(100, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = NA,
                ForeColor = Color.White,
                Font      = new Font("Segoe UI Semibold", 8.75F, FontStyle.Bold, GraphicsUnit.Point),
                UseVisualStyleBackColor = false,
                Cursor    = Cursors.Hand
            };
            notificationsTelegramTestButton.FlatAppearance.BorderSize = 0;
            notificationsTelegramTestButton.FlatAppearance.MouseOverBackColor =
                Color.FromArgb(
                    Math.Min(255, NA.R + 18),
                    Math.Min(255, NA.G + 18),
                    Math.Min(255, NA.B + 18));
            notificationsTelegramTestButton.Click += notificationsTelegramTestButton_Click;

            actionRow.Resize += (s, e) =>
            {
                notificationsTelegramTestButton.Location = new Point(
                    actionRow.Width - notificationsTelegramTestButton.Width - 20, 15);
                notificationsTelegramToggle.Location = new Point(
                    notificationsTelegramTestButton.Left - notificationsTelegramToggle.Width - 14, 16);
                notificationsTelegramStatusLabel.Location = new Point(
                    notificationsTelegramToggle.Left - notificationsTelegramStatusLabel.Width - 10, 20);
            };

            actionRow.Controls.Add(togglePrompt);
            actionRow.Controls.Add(notificationsTelegramStatusLabel);
            actionRow.Controls.Add(notificationsTelegramToggle);
            actionRow.Controls.Add(notificationsTelegramTestButton);

            card.Resize += (s, e) =>
            {
                innerDivider.Width  = card.ClientSize.Width;
                fieldsArea.Width    = card.ClientSize.Width;
                actionDivider.Width = card.ClientSize.Width;
                actionRow.Width     = card.ClientSize.Width;
                sectionDesc.Width   = card.ClientSize.Width - 40;
            };

            card.Controls.Add(sectionTitle);
            card.Controls.Add(sectionDesc);
            card.Controls.Add(innerDivider);
            card.Controls.Add(fieldsArea);
            card.Controls.Add(actionDivider);
            card.Controls.Add(actionRow);

            card.Height = 292;
            return card;
        }

        // ════════════════════════════════════════════════════════════════════════
        // SHARED HELPERS
        // ════════════════════════════════════════════════════════════════════════

        private Panel CreateSectionCard()
        {
            var card = new Panel
            {
                BackColor = NC,
                Margin    = new Padding(0),
                Padding   = new Padding(0)
            };

            card.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rc = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
                using (var path   = RoundedRect(rc, 8))
                using (var border = new Pen(NBo, 1F))
                    g.DrawPath(border, path);
            };

            card.Resize += (s, e) =>
            {
                var rc = new Rectangle(0, 0, card.Width, card.Height);
                using (var path = RoundedRect(rc, 8))
                    card.Region = new Region(path);
            };

            return card;
        }

        private CheckBox CreatePillToggle()
        {
            var toggle = new CheckBox
            {
                Appearance            = System.Windows.Forms.Appearance.Button,
                AutoSize              = false,
                Size                  = new Size(66, 28),
                Text                  = string.Empty,
                FlatStyle             = FlatStyle.Flat,
                BackColor             = NF,
                ForeColor             = NM,
                Font                  = new Font("Segoe UI Semibold", 8F, FontStyle.Bold, GraphicsUnit.Point),
                UseVisualStyleBackColor = false,
                Cursor                = Cursors.Hand,
                TextAlign             = ContentAlignment.MiddleCenter
            };
            toggle.Region = new Region(RoundedRect(new Rectangle(0, 0, toggle.Width - 1, toggle.Height - 1), toggle.Height / 2));
            toggle.FlatAppearance.BorderSize             = 0;
            toggle.FlatAppearance.BorderColor            = NBo;
            toggle.FlatAppearance.CheckedBackColor       = NA;
            toggle.FlatAppearance.MouseDownBackColor     = Color.Transparent;
            toggle.FlatAppearance.MouseOverBackColor     = Color.Transparent;
            toggle.Resize += (s, e) =>
            {
                var cb = (CheckBox)s;
                cb.Region = new Region(RoundedRect(new Rectangle(0, 0, cb.Width - 1, cb.Height - 1), cb.Height / 2));
                cb.Invalidate();
            };
            toggle.HandleCreated += (s, e) =>
            {
                var cb = (CheckBox)s;
                cb.Region = new Region(RoundedRect(new Rectangle(0, 0, cb.Width - 1, cb.Height - 1), cb.Height / 2));
                cb.Invalidate();
            };
            toggle.VisibleChanged += (s, e) =>
            {
                var cb = (CheckBox)s;
                if (cb.Visible)
                {
                    cb.Region = new Region(RoundedRect(new Rectangle(0, 0, cb.Width - 1, cb.Height - 1), cb.Height / 2));
                    cb.Invalidate();
                }
            };

            toggle.Paint += (s, e) =>
            {
                var  cb       = (CheckBox)s;
                var  g        = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool on       = cb.Checked;
                var  bgColor  = on ? NA : NF;
                var  rc       = new Rectangle(0, 0, cb.Width - 1, cb.Height - 1);

                using (var path = RoundedRect(rc, cb.Height / 2))
                using (var fill = new SolidBrush(bgColor))
                    g.FillPath(fill, path);

                if (!on)
                {
                    using (var path = RoundedRect(rc, cb.Height / 2))
                    using (var pen  = new Pen(NBo, 1F))
                        g.DrawPath(pen, path);
                }

                int knobSize = cb.Height - 8;
                int knobLeft = on ? cb.Width - knobSize - 4 : 4;
                using (var b = new SolidBrush(on ? Color.White : NM))
                    g.FillEllipse(b, new Rectangle(knobLeft, 4, knobSize, knobSize));

                string label      = on ? "ON" : "OFF";
                var    labelFont  = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold, GraphicsUnit.Point);
                var    labelBrush = new SolidBrush(on ? Color.White : NM);
                var    sf         = new StringFormat
                {
                    Alignment     = on ? StringAlignment.Near : StringAlignment.Far,
                    LineAlignment = StringAlignment.Center
                };
                int labelWidth = cb.Width - knobSize - 12;
                var labelRectF = on
                    ? new RectangleF(8,            0, labelWidth, cb.Height)
                    : new RectangleF(knobSize + 4, 0, labelWidth, cb.Height);
                g.DrawString(label, labelFont, labelBrush, labelRectF, sf);
                labelFont.Dispose();
                labelBrush.Dispose();
            };

            toggle.CheckedChanged += (s, e) => ((CheckBox)s).Invalidate();
            return toggle;
        }

        private TextBox CreateStyledTextBox(bool password)
        {
            return new TextBox
            {
                BorderStyle           = BorderStyle.FixedSingle,
                BackColor             = NF,
                ForeColor             = NW,
                Font                  = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Height                = 28,
                UseSystemPasswordChar = password,
                Margin                = new Padding(0)
            };
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d    = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X,         r.Y,          d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y,          d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d,   0, 90);
            path.AddArc(r.X,         r.Bottom - d, d, d,  90, 90);
            path.CloseFigure();
            return path;
        }

        // ════════════════════════════════════════════════════════════════════════
        // SETTINGS APPLY / UPDATE
        // ════════════════════════════════════════════════════════════════════════

        private void ApplyNotificationSettingsToUi()
        {
            if (notificationSettings == null)
                notificationSettings = new NotificationSettings();

            notificationsUiInitializing = true;
            try
            {
                notificationsWindowsToggle.Checked      = notificationSettings.WindowsNotificationsEnabled;
                notificationsTelegramTokenTextBox.Text  = notificationSettings.TelegramBotToken  ?? string.Empty;
                notificationsTelegramChatIdTextBox.Text = notificationSettings.TelegramChatId    ?? string.Empty;
                notificationsTelegramToggle.Checked     = notificationSettings.TelegramNotificationsEnabled;
            }
            finally
            {
                notificationsUiInitializing = false;
            }

            UpdateNotificationStatusLabels();
        }

        private void InitializeNotificationsNotifyIcon()
        {
            notificationsNotifyIcon = new NotifyIcon(components)
            {
                Text    = "Valhalla",
                Visible = false
            };

            try
            {
                notificationsNotifyIconImage = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                notificationsNotifyIcon.Icon = notificationsNotifyIconImage ?? SystemIcons.Information;
            }
            catch
            {
                notificationsNotifyIcon.Icon = SystemIcons.Information;
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // EVENT HANDLERS
        // ════════════════════════════════════════════════════════════════════════

        private void notificationsWindowsToggle_CheckedChanged(object sender, EventArgs e)
        {
            if (notificationsUiInitializing) return;
            notificationSettings.WindowsNotificationsEnabled = notificationsWindowsToggle.Checked;
            NotificationSettingsStore.Save(notificationSettings);
            UpdateNotificationStatusLabels();
        }

        private void notificationsTelegramToggle_CheckedChanged(object sender, EventArgs e)
        {
            if (notificationsUiInitializing) return;

            if (notificationsTelegramToggle.Checked &&
                (!TelegramNotificationService.IsValidBotToken(notificationsTelegramTokenTextBox.Text) ||
                 !TelegramNotificationService.IsValidChatId(notificationsTelegramChatIdTextBox.Text)))
            {
                notificationsTelegramToggle.Checked = false;
                MessageBox.Show(
                    "Enter a valid Telegram Bot Token and Chat ID before enabling Telegram notifications.",
                    "Telegram Notifications",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            notificationSettings.TelegramNotificationsEnabled = notificationsTelegramToggle.Checked;
            NotificationSettingsStore.Save(notificationSettings);
            UpdateNotificationStatusLabels();
        }

        private void notificationsTelegramSettingsChanged(object sender, EventArgs e)
        {
            if (notificationsUiInitializing) return;

            notificationSettings.TelegramBotToken = notificationsTelegramTokenTextBox.Text.Trim();
            notificationSettings.TelegramChatId   = notificationsTelegramChatIdTextBox.Text.Trim();

            if (notificationsTelegramToggle.Checked &&
                (!TelegramNotificationService.IsValidBotToken(notificationSettings.TelegramBotToken) ||
                 !TelegramNotificationService.IsValidChatId(notificationSettings.TelegramChatId)))
            {
                notificationsTelegramToggle.Checked               = false;
                notificationSettings.TelegramNotificationsEnabled = false;
            }

            NotificationSettingsStore.Save(notificationSettings);
            UpdateNotificationStatusLabels();
        }

        private void UpdateNotificationStatusLabels()
        {
            if (notificationsWindowsStatusLabel != null)
            {
                notificationsWindowsStatusLabel.Text      = notificationsWindowsToggle.Checked ? "Enabled" : "Disabled";
                notificationsWindowsStatusLabel.ForeColor = NM;
            }

            if (notificationsTelegramStatusLabel != null)
            {
                bool valid = TelegramNotificationService.IsValidBotToken(notificationsTelegramTokenTextBox.Text) &&
                             TelegramNotificationService.IsValidChatId(notificationsTelegramChatIdTextBox.Text);

                if (!valid && notificationsTelegramToggle.Checked)
                    notificationsTelegramToggle.Checked = false;

                notificationsTelegramStatusLabel.Text =
                    notificationsTelegramToggle.Checked ? "Enabled" :
                    (valid ? "Ready" : "Setup required");
                notificationsTelegramStatusLabel.ForeColor = NM;
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // TELEGRAM TEST
        // ════════════════════════════════════════════════════════════════════════

        private void notificationsTelegramTestButton_Click(object sender, EventArgs e)
        {
            string token  = notificationsTelegramTokenTextBox.Text.Trim();
            string chatId = notificationsTelegramChatIdTextBox.Text.Trim();

            if (!TelegramNotificationService.IsValidBotToken(token) ||
                !TelegramNotificationService.IsValidChatId(chatId))
            {
                MessageBox.Show(
                    "Enter a valid Telegram Bot Token and Chat ID before testing the connection.",
                    "Telegram Notifications",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            notificationsTelegramTestButton.Enabled    = false;
            notificationsTelegramStatusLabel.Text      = "Sending\u2026";
            notificationsTelegramStatusLabel.ForeColor = NM;

            Task.Run(async delegate
            {
                try
                {
                    await TelegramNotificationService.SendConnectionMessageAsync(
                        token, chatId, "Test", "Valhalla", "127.0.0.1", "Local");

                    BeginInvoke(new Action(delegate
                    {
                        notificationsTelegramTestButton.Enabled    = true;
                        notificationsTelegramStatusLabel.Text      = "Test sent";
                        notificationsTelegramStatusLabel.ForeColor = NM;
                        MessageBox.Show(
                            "Test message sent successfully.",
                            "Telegram Notifications",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                        UpdateNotificationStatusLabels();
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(delegate
                    {
                        notificationsTelegramTestButton.Enabled    = true;
                        notificationsTelegramStatusLabel.Text      = "Send failed";
                        notificationsTelegramStatusLabel.ForeColor = Color.FromArgb(225, 120, 120);
                        MessageBox.Show(
                            "Telegram test failed.\r\n\r\n" + ex.Message,
                            "Telegram Notifications",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        UpdateNotificationStatusLabels();
                    }));
                }
            });
        }

        // ════════════════════════════════════════════════════════════════════════
        // RUNTIME NOTIFICATION DISPATCH
        // ════════════════════════════════════════════════════════════════════════

        private void HandleNewConnectionNotification(string clientName, string tag, string ipAddress, string country)
        {
            var settings = new NotificationSettings
            {
                WindowsNotificationsEnabled  = notificationSettings != null && notificationSettings.WindowsNotificationsEnabled,
                TelegramNotificationsEnabled = notificationSettings != null && notificationSettings.TelegramNotificationsEnabled,
                TelegramBotToken             = notificationSettings?.TelegramBotToken  ?? string.Empty,
                TelegramChatId               = notificationSettings?.TelegramChatId    ?? string.Empty
            };

            if (settings.WindowsNotificationsEnabled)
                ShowWindowsConnectionNotification(clientName, tag, ipAddress, country);

            if (settings.TelegramNotificationsEnabled &&
                TelegramNotificationService.IsValidBotToken(settings.TelegramBotToken) &&
                TelegramNotificationService.IsValidChatId(settings.TelegramChatId))
            {
                Task.Run(async delegate
                {
                    try
                    {
                        await TelegramNotificationService.SendConnectionMessageAsync(
                            settings.TelegramBotToken,
                            settings.TelegramChatId,
                            clientName, tag, ipAddress, country).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogToMonitor("Telegram notification failed: " + ex.Message, LogType.Warning);
                    }
                });
            }
        }

        private void ShowWindowsConnectionNotification(string clientName, string tag, string ipAddress, string country)
        {
            try
            {
                if (notificationsNotifyIcon == null) return;
                notificationsNotifyIcon.Visible = true;
                notificationsNotifyIcon.ShowBalloonTip(
                    4000,
                    "New Client Connected",
                    "Name: "    + (string.IsNullOrWhiteSpace(clientName) ? "Unknown" : clientName) + "\r\n" +
                    "Tag: "     + (string.IsNullOrWhiteSpace(tag)        ? "Valhalla" : tag)     + "\r\n" +
                    "IP: "      + (string.IsNullOrWhiteSpace(ipAddress)  ? "Unknown" : ipAddress)  + "\r\n" +
                    "Country: " + (string.IsNullOrWhiteSpace(country)    ? "Local" : country),
                    ToolTipIcon.Info);
                notificationsNotifyIcon.Visible = false;
            }
            catch (Exception ex)
            {
                LogToMonitor("Windows notification failed: " + ex.Message, LogType.Warning);
            }
        }

        private void SaveNotificationSettingsOnClose()
        {
            if (notificationSettings == null) return;
            notificationSettings.WindowsNotificationsEnabled  = notificationsWindowsToggle  != null && notificationsWindowsToggle.Checked;
            notificationSettings.TelegramNotificationsEnabled = notificationsTelegramToggle  != null && notificationsTelegramToggle.Checked;
            notificationSettings.TelegramBotToken             = notificationsTelegramTokenTextBox  != null ? notificationsTelegramTokenTextBox.Text.Trim()  : string.Empty;
            notificationSettings.TelegramChatId               = notificationsTelegramChatIdTextBox != null ? notificationsTelegramChatIdTextBox.Text.Trim() : string.Empty;
            NotificationSettingsStore.Save(notificationSettings);
        }

    }
}
