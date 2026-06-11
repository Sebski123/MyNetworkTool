using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using NetworkingTool.Shared;

namespace NetworkingTool.Tray;

/// <summary>
/// The main tray window, built entirely in code (no Designer / resx). It is a thin presentation
/// layer: every action it offers is translated into a structured <see cref="IpcRequest"/> and sent
/// to the service via <see cref="PipeClient"/>. It performs no privileged work itself and shows the
/// service's own success/error <see cref="IpcResponse.Message"/> in an output log.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ComboBox _adapters = new();
    private readonly Button _refresh = new();
    private readonly CheckBox _physicalOnly = new();
    private readonly TextBox _details = new();
    private readonly Button _toggleAdapter = new();

    private readonly TraySettings _settings = TraySettings.Load();

    private readonly TextBox _ip = new();
    private readonly TextBox _prefix = new();
    private readonly TextBox _gateway = new();
    private readonly TextBox _dns = new();

    private readonly Button _applyStatic = new();
    private readonly Button _enableDhcp = new();
    private readonly Button _profilePrivate = new();
    private readonly Button _profilePublic = new();

    private readonly ComboBox _presets = new();
    private readonly Button _applyPreset = new();
    private readonly Button _resetProxy = new();
    private readonly TextBox _proxyStatus = new();

    private readonly TextBox _output = new();

    // Status bar: a message + an indeterminate (marquee) bar shown only while a request is in flight.
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripProgressBar _statusProgress = new();

    // Nesting counter for overlapping requests (e.g. a refresh that triggers a selection change).
    // UI-thread only — every IPC continuation resumes here — so no locking is needed.
    private int _busyCount;

    // Guards the one-time initial data load so it runs exactly once regardless of whether it is
    // triggered by the background prewarm at startup or by the window being shown.
    private bool _initialLoadStarted;

    public MainForm()
    {
        Text = $"MyNetworkTool v{ProductVersionShort()}";
        Icon = AppIcon.Load(SystemInformation.IconSize);   // custom title-bar / taskbar icon
        ClientSize = new Size(560, 745);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = true;
        MaximizeBox = false;

        BuildControls();
    }

    /// <summary>
    /// Runs the one-time initial data load (adapters, proxy presets, proxy status). It is safe to
    /// call this before the window is ever shown so the data is fetched in the background at startup
    /// and is ready the moment the user opens the GUI. Repeat calls are no-ops.
    /// </summary>
    public Task EnsureInitialDataLoadedAsync()
    {
        if (_initialLoadStarted)
        {
            return Task.CompletedTask;
        }
        _initialLoadStarted = true;

        return LoadInitialDataAsync();

        async Task LoadInitialDataAsync()
        {
            await RefreshAdaptersAsync();
            await RefreshPresetsAsync();
            await RefreshProxyStatusAsync();
        }
    }

    private void BuildControls()
    {
        const int left = 12;
        const int width = 536;
        int y = 12;

        // --- Adapters group ---
        var adaptersLabel = new Label
        {
            Text = "Network adapter",
            Location = new Point(left, y),
            Size = new Size(width, 18),
            Font = new Font(Font, FontStyle.Bold)
        };
        y += 22;

        _adapters.DropDownStyle = ComboBoxStyle.DropDownList;
        _adapters.Location = new Point(left, y);
        _adapters.Size = new Size(width, 24);
        _adapters.SelectedIndexChanged += OnAdapterSelectionChanged;
        y += 30;

        _refresh.Text = "Refresh adapters";
        _refresh.Location = new Point(left, y);
        _refresh.Size = new Size(140, 26);
        _refresh.Click += async (s, e) => await RefreshAdaptersAsync();

        // The physical-only toggle shares the refresh row. Apply the persisted value BEFORE wiring
        // CheckedChanged, so restoring it at startup does not trigger a spurious refresh.
        _physicalOnly.Text = "Show physical adapters only";
        _physicalOnly.Location = new Point(left + 150, y + 3);
        _physicalOnly.Size = new Size(width - 150, 22);
        _physicalOnly.Checked = _settings.PhysicalOnly;
        _physicalOnly.CheckedChanged += async (s, e) =>
        {
            _settings.PhysicalOnly = _physicalOnly.Checked;
            _settings.Save();
            await RefreshAdaptersAsync();
        };
        y += 36;

        // --- Current adapter state (populated when an adapter is selected) ---
        var stateLabel = new Label
        {
            Text = "Current state",
            Location = new Point(left, y),
            Size = new Size(width, 18),
            Font = new Font(Font, FontStyle.Bold)
        };
        y += 22;

        _details.Multiline = true;
        _details.ReadOnly = true;
        _details.ScrollBars = ScrollBars.Vertical;
        _details.Font = new Font(FontFamily.GenericMonospace, 8.5f);
        _details.Location = new Point(left, y);
        _details.Size = new Size(width, 116);
        _details.Text = "(select an adapter)";
        y += 122;

        _toggleAdapter.Text = "Disable adapter";
        _toggleAdapter.Location = new Point(left, y);
        _toggleAdapter.Size = new Size(160, 28);
        _toggleAdapter.Enabled = false;
        _toggleAdapter.Click += OnToggleAdapter;
        y += 38;

        // --- Static IP inputs ---
        var staticLabel = new Label
        {
            Text = "Static IP configuration",
            Location = new Point(left, y),
            Size = new Size(width, 18),
            Font = new Font(Font, FontStyle.Bold)
        };
        y += 24;

        const int fieldLabelW = 110;
        const int fieldX = left + fieldLabelW + 6;
        const int fieldW = width - fieldLabelW - 6;

        var ipLabel = new Label { Text = "IP address", Location = new Point(left, y + 3), Size = new Size(fieldLabelW, 18) };
        _ip.Location = new Point(fieldX, y);
        _ip.Size = new Size(fieldW, 24);
        y += 30;

        var prefixLabel = new Label { Text = "Prefix length", Location = new Point(left, y + 3), Size = new Size(fieldLabelW, 18) };
        _prefix.Location = new Point(fieldX, y);
        _prefix.Size = new Size(80, 24);
        _prefix.Text = "24";
        y += 30;

        var gatewayLabel = new Label { Text = "Gateway", Location = new Point(left, y + 3), Size = new Size(fieldLabelW, 18) };
        _gateway.Location = new Point(fieldX, y);
        _gateway.Size = new Size(fieldW, 24);
        y += 30;

        var dnsLabel = new Label { Text = "DNS servers", Location = new Point(left, y + 3), Size = new Size(fieldLabelW, 18) };
        _dns.Location = new Point(fieldX, y);
        _dns.Size = new Size(fieldW, 24);
        y += 26;

        var dnsHint = new Label
        {
            Text = "comma-separated",
            Location = new Point(fieldX, y),
            Size = new Size(fieldW, 16),
            ForeColor = SystemColors.GrayText
        };
        y += 24;

        // --- Action buttons (static / dhcp / profile) ---
        _applyStatic.Text = "Apply static IP";
        _applyStatic.Location = new Point(left, y);
        _applyStatic.Size = new Size(130, 28);
        _applyStatic.Click += OnApplyStaticIp;

        _enableDhcp.Text = "Enable DHCP";
        _enableDhcp.Location = new Point(left + 136, y);
        _enableDhcp.Size = new Size(120, 28);
        _enableDhcp.Click += OnEnableDhcp;

        _profilePrivate.Text = "Set profile Private";
        _profilePrivate.Location = new Point(left + 262, y);
        _profilePrivate.Size = new Size(130, 28);
        _profilePrivate.Click += OnSetProfilePrivate;

        _profilePublic.Text = "Set profile Public";
        _profilePublic.Location = new Point(left + 398, y);
        _profilePublic.Size = new Size(130, 28);
        _profilePublic.Click += OnSetProfilePublic;
        y += 40;

        // --- Proxy group ---
        var proxyLabel = new Label
        {
            Text = "Proxy preset",
            Location = new Point(left, y),
            Size = new Size(width, 18),
            Font = new Font(Font, FontStyle.Bold)
        };
        y += 24;

        _presets.DropDownStyle = ComboBoxStyle.DropDownList;
        _presets.Location = new Point(left, y);
        _presets.Size = new Size(width, 24);
        y += 30;

        _applyPreset.Text = "Apply proxy preset";
        _applyPreset.Location = new Point(left, y);
        _applyPreset.Size = new Size(150, 28);
        _applyPreset.Click += OnApplyProxyPreset;

        _resetProxy.Text = "Reset proxy";
        _resetProxy.Location = new Point(left + 156, y);
        _resetProxy.Size = new Size(120, 28);
        _resetProxy.Click += OnResetProxy;
        y += 38;

        var proxyStatusLabel = new Label
        {
            Text = "Current proxy (machine)",
            Location = new Point(left, y),
            Size = new Size(width, 18),
            Font = new Font(Font, FontStyle.Bold)
        };
        y += 22;

        _proxyStatus.Multiline = true;
        _proxyStatus.ReadOnly = true;
        _proxyStatus.Location = new Point(left, y);
        _proxyStatus.Size = new Size(width, 42);
        _proxyStatus.Text = "Loading current proxy status…";
        y += 48;

        // --- Output log (fills the remaining space at the bottom) ---
        var outputLabel = new Label
        {
            Text = "Output",
            Location = new Point(left, y),
            Size = new Size(width, 18),
            Font = new Font(Font, FontStyle.Bold)
        };
        y += 22;

        _output.Multiline = true;
        _output.ReadOnly = true;
        _output.ScrollBars = ScrollBars.Vertical;
        _output.Location = new Point(left, y);
        _output.Size = new Size(width, ClientSize.Height - y - 12);
        _output.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

        // --- Status bar (docked at the very bottom) ---
        // The label fills the bar (Spring) with the marquee pinned to the right; the marquee is shown
        // only while a request is in flight. Grow the form by the strip's height AFTER sizing _output
        // so the strip gets its own band and no other control shifts.
        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Text = "Ready";

        _statusProgress.Style = ProgressBarStyle.Marquee;
        _statusProgress.Visible = false;

        _statusStrip.SizingGrip = false;
        _statusStrip.Items.Add(_statusLabel);
        _statusStrip.Items.Add(_statusProgress);

        ClientSize = new Size(ClientSize.Width, ClientSize.Height + _statusStrip.Height);

        Controls.AddRange(new Control[]
        {
            adaptersLabel, _adapters, _refresh, _physicalOnly,
            stateLabel, _details, _toggleAdapter,
            staticLabel,
            ipLabel, _ip,
            prefixLabel, _prefix,
            gatewayLabel, _gateway,
            dnsLabel, _dns, dnsHint,
            _applyStatic, _enableDhcp, _profilePrivate, _profilePublic,
            proxyLabel, _presets, _applyPreset, _resetProxy,
            proxyStatusLabel, _proxyStatus,
            outputLabel, _output,
            _statusStrip // docked controls added last
        });
    }

    // ---------------------------------------------------------------------
    // Busy indicator
    // ---------------------------------------------------------------------

    /// <summary>
    /// Runs an IPC action with the status bar showing <paramref name="busyText"/> and a marquee while
    /// it is in flight, restoring "Ready" when it (and any nested action) completes. The whole body
    /// runs inside the try so an early <c>return</c> or exception can never leak the busy state.
    /// </summary>
    private async Task RunBusyAsync(string busyText, Func<Task> action)
    {
        SetBusy(busyText, true);
        try
        {
            await action();
        }
        finally
        {
            SetBusy(null, false);
        }
    }

    /// <summary>
    /// Adjusts the shared busy state. A nesting counter handles overlapping requests (e.g. a refresh
    /// that synchronously triggers an adapter-selection read); the bar reverts to "Ready" only when
    /// the outermost action finishes. UI-thread only, so no synchronization is needed.
    /// </summary>
    private void SetBusy(string? text, bool busy)
    {
        _busyCount += busy ? 1 : -1;
        bool isBusy = _busyCount > 0;

        _statusProgress.Visible = isBusy;
        _statusLabel.Text = isBusy ? (text ?? _statusLabel.Text) : "Ready";

        // Block mutating actions while a request is in flight; the read-only Refresh stays available.
        SetMutationControlsEnabled(!isBusy);
    }

    /// <summary>Enables/disables the buttons that change machine state (Refresh is intentionally left on).</summary>
    private void SetMutationControlsEnabled(bool enabled)
    {
        _applyStatic.Enabled = enabled;
        _enableDhcp.Enabled = enabled;
        _profilePrivate.Enabled = enabled;
        _profilePublic.Enabled = enabled;
        _applyPreset.Enabled = enabled;
        _resetProxy.Enabled = enabled;

        // The enable/disable toggle is only meaningful when an adapter is selected; preserve that.
        _toggleAdapter.Enabled = enabled && _adapters.SelectedItem is AdapterInfo;
    }

    /// <summary>Hide instead of dispose so the tray can reopen the same window.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    // ---------------------------------------------------------------------
    // Refresh helpers
    // ---------------------------------------------------------------------

    private Task RefreshAdaptersAsync() => RunBusyAsync("Loading adapters…", async () =>
    {
        try
        {
            string? previousId = (_adapters.SelectedItem as AdapterInfo)?.Id;

            IpcResponse response = await PipeClient.SendAsync(new IpcRequest
            {
                Action = IpcAction.ListAdapters,
                PhysicalOnly = _physicalOnly.Checked,
            });
            if (response.Success)
            {
                _adapters.Items.Clear();
                if (response.Adapters is { Length: > 0 })
                {
                    foreach (AdapterInfo adapter in response.Adapters)
                    {
                        _adapters.Items.Add(adapter);
                    }

                    // Keep the user on the same adapter across a refresh when it is still present.
                    // Clearing the items above reset the selection to -1, so assigning here always
                    // raises SelectedIndexChanged and refreshes the details panel exactly once.
                    int restored = previousId is null
                        ? 0
                        : Array.FindIndex(response.Adapters,
                            a => string.Equals(a.Id, previousId, StringComparison.OrdinalIgnoreCase));
                    _adapters.SelectedIndex = restored >= 0 ? restored : 0;
                }
            }
            AppendLog(response);
        }
        catch (Exception ex)
        {
            AppendLog(IpcResponse.Fail("Refresh adapters failed: " + ex.Message));
        }
    });

    private Task RefreshPresetsAsync() => RunBusyAsync("Loading proxy presets…", async () =>
    {
        try
        {
            IpcResponse response = await PipeClient.SendAsync(new IpcRequest { Action = IpcAction.ListProxyPresets });
            if (response.Success)
            {
                _presets.Items.Clear();
                _presets.DisplayMember = "Name";
                if (response.Presets is { Length: > 0 })
                {
                    foreach (ProxyPresetInfo preset in response.Presets)
                    {
                        _presets.Items.Add(preset);
                    }
                    _presets.SelectedIndex = 0;
                }
            }
            AppendLog(response);
        }
        catch (Exception ex)
        {
            AppendLog(IpcResponse.Fail("Refresh presets failed: " + ex.Message));
        }
    });

    private Task RefreshProxyStatusAsync() => RunBusyAsync("Reading proxy status…", async () =>
    {
        try
        {
            IpcResponse response = await PipeClient.SendAsync(new IpcRequest { Action = IpcAction.GetProxyStatus });
            _proxyStatus.Text = response.Success ? response.Message : "Could not read proxy status: " + response.Message;
            AppendLog(response);
        }
        catch (Exception ex)
        {
            _proxyStatus.Text = "Could not read proxy status: " + ex.Message;
            AppendLog(IpcResponse.Fail("Refresh proxy status failed: " + ex.Message));
        }
    });

    // ---------------------------------------------------------------------
    // Adapter selection: show live state + set the enable/disable toggle
    // ---------------------------------------------------------------------

    private async void OnAdapterSelectionChanged(object? sender, EventArgs e)
    {
        if (_adapters.SelectedItem is not AdapterInfo a)
        {
            _toggleAdapter.Enabled = false;
            _details.Text = "(select an adapter)";
            return;
        }

        // The toggle's meaning is derived from the adapter's current status, which we already have
        // from the list — "Disabled" means the only sensible action is to enable it again.
        bool isDisabled = string.Equals(a.Status, "Disabled", StringComparison.OrdinalIgnoreCase);
        _toggleAdapter.Text = isDisabled ? "Enable adapter" : "Disable adapter";
        _toggleAdapter.Enabled = true;

        _details.Text = "Loading current state…";
        await RunBusyAsync("Reading adapter state…", async () =>
        {
            try
            {
                IpcResponse response = await PipeClient.SendAsync(new IpcRequest
                {
                    Action = IpcAction.GetAdapterDetails,
                    InterfaceId = a.Id,
                });

                // The selection may have changed (or the list refreshed) while we awaited; only render
                // if this adapter is still the selected one.
                if (!ReferenceEquals(_adapters.SelectedItem, a)) return;

                _details.Text = response.Success && response.Details is not null
                    ? FormatDetails(a, response.Details)
                    : "Could not read adapter state: " + response.Message;
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_adapters.SelectedItem, a))
                {
                    _details.Text = "Could not read adapter state: " + ex.Message;
                }
            }
        });
    }

    private static string FormatDetails(AdapterInfo info, AdapterDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Status:   {info.Status}");
        sb.AppendLine($"DHCP:     {(d.IsDhcpEnabled ? "Enabled" : "Disabled")}");

        if (d.Ipv4Addresses.Length > 0)
        {
            foreach (IpAddressInfo ip in d.Ipv4Addresses)
            {
                sb.AppendLine($"IPv4:     {ip.Address}/{ip.PrefixLength}  (mask {PrefixToMask(ip.PrefixLength)})");
            }
        }
        else
        {
            sb.AppendLine("IPv4:     (none)");
        }

        sb.AppendLine($"Gateway:  {(string.IsNullOrEmpty(d.Gateway) ? "(none)" : d.Gateway)}");
        sb.AppendLine($"DNS:      {(d.DnsServers.Length > 0 ? string.Join(", ", d.DnsServers) : "(none)")}");
        sb.AppendLine($"Profile:  {(string.IsNullOrEmpty(d.ProfileName) ? "(not connected)" : $"{d.ProfileName} ({d.ProfileCategory})")}");

        return sb.ToString();
    }

    /// <summary>
    /// The product version without the build-metadata suffix. <see cref="Application.ProductVersion"/>
    /// is the informational version, which is "1.2.7+&lt;commit-sha&gt;" for a normal git build but can
    /// be just "1.2.7" when built without source-revision metadata — so trim at '+' only when present
    /// rather than assuming it (which threw an out-of-range exception on a metadata-less build).
    /// </summary>
    private static string ProductVersionShort()
    {
        string version = Application.ProductVersion;
        int plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    /// <summary>Converts a CIDR prefix length (0..32) to a dotted IPv4 subnet mask.</summary>
    private static string PrefixToMask(int prefix)
    {
        if (prefix < 0 || prefix > 32) return "n/a";
        uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
        return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
    }

    private async void OnToggleAdapter(object? sender, EventArgs e)
    {
        if (_adapters.SelectedItem is not AdapterInfo a)
        {
            AppendLog(IpcResponse.Fail("Select an adapter first."));
            return;
        }

        // Enable a disabled adapter; otherwise disable it (after confirmation, since disabling can
        // drop the connection the user is relying on).
        bool enable = string.Equals(a.Status, "Disabled", StringComparison.OrdinalIgnoreCase);
        if (!enable)
        {
            DialogResult confirm = MessageBox.Show(
                this,
                $"Disable adapter '{a.Name}'?\r\n\r\nThis will drop any network connection it provides.",
                "Confirm disable adapter",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
        }

        // RunBusyAsync disables the mutation controls (including this toggle) for the duration and
        // restores them when done, so no manual enable/disable juggling is needed here.
        await RunBusyAsync(enable ? "Enabling adapter…" : "Disabling adapter…", async () =>
        {
            try
            {
                AppendLog(await PipeClient.SendAsync(new IpcRequest
                {
                    Action = IpcAction.SetAdapterEnabled,
                    InterfaceId = a.Id,
                    Enable = enable,
                }));

                // Reflect the new status (and refreshed state) in the list and details panel.
                await RefreshAdaptersAsync();
            }
            catch (Exception ex)
            {
                AppendLog(IpcResponse.Fail("Toggle adapter failed: " + ex.Message));
            }
        });
    }

    // ---------------------------------------------------------------------
    // Button handlers
    // ---------------------------------------------------------------------

    private async void OnApplyStaticIp(object? sender, EventArgs e)
    {
        try
        {
            if (_adapters.SelectedItem is not AdapterInfo a)
            {
                AppendLog(IpcResponse.Fail("Select an adapter first."));
                return;
            }

            if (!int.TryParse(_prefix.Text.Trim(), out int prefix))
            {
                AppendLog(IpcResponse.Fail("Prefix length must be a number (e.g. 24)."));
                return;
            }

            string gateway = _gateway.Text.Trim();
            string[]? dns = _dns.Text
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (dns.Length == 0) dns = null;

            var request = new IpcRequest
            {
                Action = IpcAction.SetStaticIp,
                InterfaceId = a.Id,
                IpAddress = _ip.Text.Trim(),
                PrefixLength = prefix,
                Gateway = string.IsNullOrEmpty(gateway) ? null : gateway,
                DnsServers = dns
            };

            await RunBusyAsync("Applying static IP…", async () =>
            {
                IpcResponse response = await PipeClient.SendAsync(request);
                AppendLog(response);

                if (response.Success)
                {
                    await RefreshAdaptersAsync();
                }
            });
        }
        catch (Exception ex)
        {
            AppendLog(IpcResponse.Fail("Apply static IP failed: " + ex.Message));
        }
    }

    private async void OnEnableDhcp(object? sender, EventArgs e)
    {
        try
        {
            if (_adapters.SelectedItem is not AdapterInfo a)
            {
                AppendLog(IpcResponse.Fail("Select an adapter first."));
                return;
            }

            await RunBusyAsync("Enabling DHCP…", async () =>
            {
                IpcResponse response = await PipeClient.SendAsync(new IpcRequest
                {
                    Action = IpcAction.SetDhcp,
                    InterfaceId = a.Id
                });
                AppendLog(response);

                if (response.Success)
                {
                    await RefreshAdaptersAsync();
                }
            });
        }
        catch (Exception ex)
        {
            AppendLog(IpcResponse.Fail("Enable DHCP failed: " + ex.Message));
        }
    }

    private async void OnSetProfilePrivate(object? sender, EventArgs e) => await SetProfileAsync("Private");

    private async void OnSetProfilePublic(object? sender, EventArgs e) => await SetProfileAsync("Public");

    private async Task SetProfileAsync(string category)
    {
        try
        {
            if (_adapters.SelectedItem is not AdapterInfo a)
            {
                AppendLog(IpcResponse.Fail("Select an adapter first."));
                return;
            }

            await RunBusyAsync($"Setting profile {category}…", async () =>
            {
                IpcResponse response = await PipeClient.SendAsync(new IpcRequest
                {
                    Action = IpcAction.SetNetworkProfile,
                    InterfaceId = a.Id,
                    Category = category
                });
                AppendLog(response);

                if (response.Success)
                {
                    await RefreshAdaptersAsync();
                }
            });
        }
        catch (Exception ex)
        {
            AppendLog(IpcResponse.Fail("Set network profile failed: " + ex.Message));
        }
    }

    private async void OnApplyProxyPreset(object? sender, EventArgs e)
    {
        try
        {
            if (_presets.SelectedItem is not ProxyPresetInfo preset)
            {
                AppendLog(IpcResponse.Fail("Select a proxy preset first."));
                return;
            }

            await RunBusyAsync("Applying proxy preset…", async () => AppendLog(await PipeClient.SendAsync(new IpcRequest
            {
                Action = IpcAction.SetProxyPreset,
                PresetName = preset.Name
            })));
            await RefreshProxyStatusAsync();
        }
        catch (Exception ex)
        {
            AppendLog(IpcResponse.Fail("Apply proxy preset failed: " + ex.Message));
        }
    }

    private async void OnResetProxy(object? sender, EventArgs e)
    {
        try
        {
            await RunBusyAsync("Resetting proxy…", async () =>
                AppendLog(await PipeClient.SendAsync(new IpcRequest { Action = IpcAction.ResetProxy })));
            await RefreshProxyStatusAsync();
        }
        catch (Exception ex)
        {
            AppendLog(IpcResponse.Fail("Reset proxy failed: " + ex.Message));
        }
    }

    // ---------------------------------------------------------------------
    // Output
    // ---------------------------------------------------------------------

    /// <summary>
    /// Prepends a timestamped status line for the response and surfaces the service's own message
    /// to the user, including adapter/preset counts when the response carried them.
    /// </summary>
    private void AppendLog(IpcResponse r)
    {
        string tag = r.Success ? "[OK]" : "[ERR]";
        string line = $"{DateTime.Now:HH:mm:ss} {tag} {r.Message}";

        if (r.Adapters is not null)
        {
            line += $"  (adapters: {r.Adapters.Length})";
        }
        if (r.Presets is not null)
        {
            line += $"  (presets: {r.Presets.Length})";
        }

        _output.AppendText(line + Environment.NewLine);
    }
}
