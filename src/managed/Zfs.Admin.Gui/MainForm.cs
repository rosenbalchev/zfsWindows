using Zfs.Admin;

namespace Zfs.Admin.Gui;

/// <summary>
/// OpenZFS for Windows administration.  Every pool and dataset change goes
/// through ZfsWinBroker (validated and written to the event log); the
/// user-mode tab edits the ZfsWinUserMode mount configuration and controls
/// that service.  Both editions show the same tabs: where an edition gets a
/// value differently, the broker hides it.
/// </summary>
sealed partial class MainForm : Form
{
    readonly AdminClient client = new();
    readonly ToolStripStatusLabel statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel editionStatus = new() { TextAlign = ContentAlignment.MiddleRight };
    readonly ToolStripLabel editionBadge = new() { Alignment = ToolStripItemAlignment.Right, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer autoRefresh = new() { Interval = 5000 };
    TabPage userModePage = null!;
    string edition = "";
    bool refreshing;

    /// <summary>--screenshots: render every tab to a PNG in this folder, then close.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? ScreenshotDirectory { get; init; }

    // Pools
    readonly ListView pools = NewList(("Pool", 140), ("Health", 80), ("Size", 90), ("Allocated", 90), ("Free", 90), ("Capacity", 70), ("Fragmentation", 90), ("Dedup", 70));
    readonly TreeView poolTree = new() { Dock = DockStyle.Fill, HideSelection = false };
    readonly Label scanLabel = new() { AutoSize = true, Padding = new Padding(4) };

    // Datasets
    readonly ListView datasets = NewList(("Dataset", 220), ("Used", 90), ("Available", 90), ("Compression", 90), ("Mounted at", 160));
    readonly ComboBox compression = Choice(AdminClient.CompressionValues);
    readonly ComboBox dedup = Choice(AdminClient.DedupValues);
    readonly ComboBox sync = Choice(AdminClient.SyncValues);
    readonly ListView properties = NewList(("Property", 160), ("Value", 200), ("Source", 120));

    // Devices
    readonly ComboBox devicePool = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly TreeView deviceTree = new() { Dock = DockStyle.Fill, HideSelection = false };
    readonly Label deviceHelp = new() { AutoSize = true, MaximumSize = new Size(300, 0), Padding = new Padding(4) };

    // User mode
    readonly Label serviceLabel = new() { AutoSize = true, Padding = new Padding(4) };
    readonly ListView mounts = NewList(("Name", 100), ("Source", 200), ("Pool", 100), ("Dataset", 140), ("Mount point", 100), ("Read-only", 70));

    bool brokerAvailable;

    public MainForm()
    {
        Text = "OpenZFS for Windows";
        Size = new Size(1100, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont;

        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        var refresh = new ToolStripButton("Refresh") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        refresh.Click += async (_, _) => await RefreshAllAsync();
        var save = new ToolStripButton("Save report…") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        save.Click += async (_, _) => await SaveReportAsync();
        tools.Items.AddRange([refresh, save, editionBadge]);
        var status = new StatusStrip();
        status.Items.AddRange([statusText, editionStatus]);

        tabs.TabPages.Add(OverviewPage());
        tabs.TabPages.Add(PoolsPage());
        tabs.TabPages.Add(DatasetsPage());
        tabs.TabPages.Add(SnapshotsPage());
        tabs.TabPages.Add(DevicesPage());
        tabs.TabPages.Add(IntegrityPage());
        tabs.TabPages.Add(RatesPage());
        tabs.TabPages.Add(userModePage = UserModePage());
        RegisterWebHosts();
        Controls.Add(tabs);
        Controls.Add(tools);
        Controls.Add(status);
        Shown += async (_, _) =>
        {
            edition = Edition.Detect();
            editionBadge.Text = edition;
            editionStatus.Text = edition;
            await RefreshAllAsync();
            if (ScreenshotDirectory is not null)
                await TakeScreenshotsAsync(ScreenshotDirectory);
            else
                autoRefresh.Start();
        };
        // Every 5 s: the tab in view, never while minimized or busy.
        autoRefresh.Tick += async (_, _) => await RefreshVisibleAsync();
        tabs.SelectedIndexChanged += async (_, _) =>
        {
            MoveWebViewToSelectedTab();
            await RefreshVisibleAsync();
        };
        FormClosing += OnClosing;
        _ = InitializeWebViewAsync();
    }

    async Task RefreshVisibleAsync()
    {
        if (refreshing || WindowState == FormWindowState.Minimized || !brokerAvailable || !tabs.Enabled)
            return;
        refreshing = true;
        TabPage page = tabs.SelectedTab!;
        try
        {
            await (page.Text switch
            {
                "Overview" => ShowOverviewAsync(),
                "Integrity" => ShowIntegrityAsync(),
                "Redundancy" => ShowDevicesAsync(),
                "Pools" => ShowPoolAsync(),
                _ => Task.CompletedTask,
            });
        }
        catch (AdminException e)
        {
            BackgroundError(page.Text, e);
        }
        finally
        {
            refreshing = false;
            RenderSelectedWebPage();
        }
    }

    /// <summary>A refresh that failed on its own: the status bar says which operation, no dialog.</summary>
    void BackgroundError(string what, AdminException e)
    {
        statusText.Text = $"{what}: {e.Message}";
        if (e.Code == ErrorCode.BrokerUnavailable)
            brokerAvailable = false;
    }

    // --- pages --------------------------------------------------------------

    TabPage PoolsPage()
    {
        var page = new TabPage("Pools");
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 220 };
        split.Panel1.Controls.Add(pools);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        foreach (var (label, action) in new[] { ("Start scrub", "start"), ("Pause scrub", "pause"), ("Stop scrub", "stop") })
        {
            var b = new Button { Text = label, AutoSize = true };
            b.Click += async (_, _) =>
            {
                if (SelectedPool() is string pool)
                    await RunAsync($"{label} on {pool}", () => client.ScrubAsync(pool, action));
            };
            buttons.Controls.Add(b);
        }
        buttons.Controls.Add(scanLabel);
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(poolTree);
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        split.Panel2.Controls.Add(bottom);
        page.Controls.Add(split);
        pools.SelectedIndexChanged += async (_, _) => { if (!applyingWebAction) await ShowPoolAsync(); };
        return page;
    }

    TabPage DatasetsPage()
    {
        var page = new TabPage("Datasets");
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 640 };
        split.Panel1.Controls.Add(datasets);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        var features = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(4) };
        AddFeature(features, "Compression:", compression, "compression");
        AddFeature(features, "Deduplication:", dedup, "dedup");
        AddFeature(features, "Sync writes:", sync, "sync");
        right.Controls.Add(new GroupBox { Text = "Features (new writes)", Dock = DockStyle.Fill, AutoSize = true, Controls = { features } });
        right.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(400, 0), Padding = new Padding(4),
            Text = "Changes apply to data written afterwards; existing blocks keep their compression and dedup state.  " +
                   "Dedup needs RAM for its table and pays off only for genuinely duplicate data.  " +
                   "sync=disabled is faster but loses the last seconds of writes on a power cut.",
        });
        right.Controls.Add(properties);
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        split.Panel2.Controls.Add(right);
        page.Controls.Add(split);
        datasets.SelectedIndexChanged += async (_, _) => { if (!applyingWebAction) await ShowDatasetAsync(); };
        return page;
    }

    void AddFeature(TableLayoutPanel grid, string label, ComboBox box, string property)
    {
        var apply = new Button { Text = "Apply", AutoSize = true };
        apply.Click += async (_, _) =>
        {
            if (SelectedDataset() is string ds && box.Text is { Length: > 0 } value)
                await RunAsync($"Set {property}={value} on {ds}", () => client.SetDatasetPropertyAsync(ds, property, value));
        };
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
        grid.Controls.Add(box);
        grid.Controls.Add(apply);
    }

    TabPage DevicesPage()
    {
        var page = new TabPage("Redundancy");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        top.Controls.Add(new Label { Text = "Pool:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) });
        top.Controls.Add(devicePool);
        devicePool.SelectedIndexChanged += async (_, _) => { if (!applyingWebAction) await ShowDevicesAsync(); };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, Width = 320, Padding = new Padding(8) };
        void Action(string text, Func<Task> handler)
        {
            // Two lines' height: the longer captions wrap instead of being cut.
            var b = new Button { Text = text, Width = 290, Height = 44 };
            b.Click += async (_, _) => await handler();
            actions.Controls.Add(b);
        }
        Action("Add mirror device to selected disk…", AddMirrorAsync);
        Action("Remove selected disk from its mirror", DetachMirrorAsync);
        Action("Add read cache (L2ARC)…", () => AddCacheAsync("cache"));
        Action("Add write log (SLOG)…", () => AddCacheAsync("log"));
        Action("Remove selected cache or log device", RemoveCacheAsync);
        deviceHelp.Text = "A mirror keeps a full copy on every member; ZFS resilvers a new member automatically.  " +
                          "A read cache (L2ARC) is a fast device that holds data evicted from RAM.  " +
                          "A write log (SLOG) speeds up synchronous writes; mirror it, because it holds writes not yet on the pool.";
        actions.Controls.Add(deviceHelp);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        // The vdev tree above, copies and drive health below (more rows).
        Shown += (_, _) => split.SplitterDistance = Math.Max(split.Panel1MinSize, split.Height * 2 / 5);
        split.Panel1.Controls.Add(deviceTree);
        split.Panel2.Controls.Add(RedundancyPanel());
        page.Controls.Add(split);
        page.Controls.Add(actions);
        page.Controls.Add(top);
        return page;
    }

    TabPage UserModePage()
    {
        var page = new TabPage("User mode");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        top.Controls.Add(serviceLabel);
        foreach (var (label, action) in new[] { ("Start", "start"), ("Stop (unmount and export)", "stop"), ("Restart", "restart") })
        {
            var b = new Button { Text = label, AutoSize = true };
            b.Click += async (_, _) => await RunAsync($"{label} {UserModeMounts.ServiceName}", () => UserModeService.ControlAsync(action));
            top.Controls.Add(b);
        }
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
        var add = new Button { Text = "Add mount…", AutoSize = true };
        var edit = new Button { Text = "Edit…", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        add.Click += (_, _) => EditMount(null);
        edit.Click += (_, _) => { if (SelectedMount() is UserModeMount m) EditMount(m); };
        remove.Click += (_, _) =>
        {
            if (SelectedMount() is not UserModeMount m || !Confirm($"Remove the mount '{m.Name}'?\r\n\r\nThe pool is not touched; restart the service to unmount it."))
                return;
            UserModeMounts.Machine().Remove(m.Name);
            ShowUserMode();
        };
        bottom.Controls.AddRange([add, edit, remove, new Label
        {
            AutoSize = true, Padding = new Padding(8, 6, 0, 0),
            Text = "Mount changes take effect when the service restarts.  Mounts made with zfs mount (or zpool import) are listed here too, as auto-<dataset>; pools are administered on the other tabs, as in the kernel-driver edition.",
            MaximumSize = new Size(700, 0),
        }]);
        page.Controls.Add(mounts);
        page.Controls.Add(bottom);
        page.Controls.Add(top);
        return page;
    }

    // --- refresh ------------------------------------------------------------

    async Task RefreshAllAsync()
    {
        UseWaitCursor = true;
        try
        {
            try
            {
                var version = await client.VersionAsync();
                brokerAvailable = true;
                statusText.Text = $"ZfsWinBroker {version?["broker"]}, OpenZFS {version?["kernel"] ?? version?["userland"]}";
            }
            catch (AdminException e) when (e.Code == ErrorCode.BrokerUnavailable)
            {
                brokerAvailable = false;
                statusText.Text = UserModeService.State() == "NotInstalled" ? "ZfsWinBroker is not running (net start ZfsWinBroker)." : "ZfsWinBroker is not running (net start ZfsWinBroker).  The User mode tab manages the ZfsWinUserMode service, which pool administration also needs.";
            }
            foreach (TabPage p in tabs.TabPages)
                p.Enabled = brokerAvailable || p == userModePage;
            if (!brokerAvailable && UserModeService.State() != "NotInstalled")
                tabs.SelectedTab = userModePage;
            if (brokerAvailable)
            {
                await ShowPoolsAsync();
                await ShowDatasetsAsync();
                await ShowSnapshotDatasetsAsync();
                await ShowOverviewAsync();
                await ShowIntegrityAsync();
            }
            ShowUserMode();
        }
        catch (AdminException e)
        {
            BackgroundError("Refresh", e);
        }
        finally
        {
            UseWaitCursor = false;
            RenderSelectedWebPage();
        }
    }

    async Task ShowPoolsAsync()
    {
        string? selected = SelectedPool();
        var list = await client.PoolsAsync();
        pools.BeginUpdate();
        pools.Items.Clear();
        foreach (PoolInfo p in list)
        {
            var item = new ListViewItem([p.Name, p.Health ?? "", Format.Bytes(p.Size), Format.Bytes(p.Allocated), Format.Bytes(p.Free),
                p.CapacityPercent is long c ? $"{c}%" : "", p.FragmentationPercent is long f ? $"{f}%" : "", p.DedupRatio ?? ""]) { Tag = p.Name };
            if (p.Health is not "ONLINE")
                item.ForeColor = Color.Firebrick;
            pools.Items.Add(item);
            item.Selected = p.Name == selected;
        }
        if (pools.SelectedItems.Count == 0 && pools.Items.Count > 0)
            pools.Items[0].Selected = true;
        pools.EndUpdate();
        foreach (ComboBox box in new[] { devicePool, integrityPool, ratesPool })
        {
            object? keep = box.SelectedItem;
            box.Items.Clear();
            box.Items.AddRange(list.Select(p => (object)p.Name).ToArray());
            if (box.Items.Count > 0)
                box.SelectedItem = keep is not null && box.Items.Contains(keep) ? keep : selected is not null && box.Items.Contains(selected) ? selected : box.Items[0];
        }
        await ShowPoolAsync();
    }

    async Task ShowPoolAsync()
    {
        if (SelectedPool() is not string pool)
            return;
        PoolStatusInfo? s = await client.StatusAsync(pool);
        FillTree(poolTree, s);
        ScanInfo? scan = s?.Scan;
        scanLabel.Text = scan is null ? "No scrub has run." :
            $"{scan.Function?.ToLowerInvariant()} {scan.State?.ToLowerInvariant()}{(scan.Paused ? " (paused)" : "")}" +
            (scan.ToExamine is > 0 && scan.Examined is long ex ? $", {100.0 * ex / scan.ToExamine.Value:0.0}% examined" : "") +
            (scan.Errors is long er ? $", {er} errors" : "");
    }

    async Task ShowDevicesAsync()
    {
        if (devicePool.SelectedItem is not string pool)
            return;
        PoolStatusInfo? status = await client.StatusAsync(pool);
        FillTree(deviceTree, status);
        await ShowRedundancyAsync(pool, status);
    }

    static void FillTree(TreeView tree, PoolStatusInfo? s)
    {
        tree.BeginUpdate();
        tree.Nodes.Clear();
        if (s is not null)
        {
            var root = tree.Nodes.Add($"{s.Name}  ({s.State}{(s.ErrorCount > 0 ? $", {s.ErrorCount} data errors" : "")})");
            foreach (VdevInfo v in s.Vdevs.SelectMany(r => r.Type == "root" ? r.Children : [r]))
                Add(root.Nodes, v, "data");
            foreach (var (title, role, group) in new[] { ("write log (SLOG)", "log", s.Logs), ("read cache (L2ARC)", "cache", s.Caches),
                         ("spares", "spare", s.Spares), ("special", "special", s.Special), ("dedup", "dedup", s.Dedup) })
            {
                if (group.Count == 0)
                    continue;
                var node = root.Nodes.Add(title);
                foreach (VdevInfo v in group)
                    Add(node.Nodes, v, role);
            }
            if (s.Status is { Length: > 0 })
                root.Nodes.Add($"status: {s.Status}");
            tree.ExpandAll();
        }
        tree.EndUpdate();

        static void Add(TreeNodeCollection nodes, VdevInfo v, string role, VdevInfo? parent = null)
        {
            string errors = v.ReadErrors + v.WriteErrors + v.ChecksumErrors > 0 ? $"  errors r/w/c {v.ReadErrors}/{v.WriteErrors}/{v.ChecksumErrors}" : "";
            var node = nodes.Add($"{(v.IsLeaf ? v.Path ?? v.Name : v.Name)}  {v.State}{errors}");
            node.Tag = new DeviceNode(v, role, parent);
            if (v.State is not ("ONLINE" or null))
                node.ForeColor = Color.Firebrick;
            foreach (VdevInfo c in v.Children)
                Add(node.Nodes, c, role, v);
        }
    }

    sealed record DeviceNode(VdevInfo Vdev, string Role, VdevInfo? Parent = null);

    async Task ShowDatasetsAsync()
    {
        string? selected = SelectedDataset();
        var list = (await client.DatasetsAsync()).Where(d => !IsTemporary(d.Name)).ToList();
        datasets.BeginUpdate();
        datasets.Items.Clear();
        foreach (DatasetInfo d in list)
        {
            var item = new ListViewItem([d.Name, Format.Bytes(d.Used), Format.Bytes(d.Available), d.Compression ?? "",
                d.Mounted ? d.DriveLetter is { Length: > 0 } l ? l : d.Mountpoint ?? "" : "(not mounted)"]) { Tag = d.Name };
            datasets.Items.Add(item);
            item.Selected = d.Name == selected;
        }
        if (datasets.SelectedItems.Count == 0 && datasets.Items.Count > 0)
            datasets.Items[0].Selected = true;
        datasets.EndUpdate();
        await ShowDatasetAsync();
    }

    async Task ShowDatasetAsync()
    {
        if (SelectedDataset() is not string ds)
            return;
        var props = await client.DatasetPropertiesAsync(ds);
        properties.BeginUpdate();
        properties.Items.Clear();
        foreach (PropertyInfo p in props)
            properties.Items.Add(new ListViewItem([p.Name, p.Value ?? "", p.Source ?? ""]));
        properties.EndUpdate();
        compression.Text = props.FirstOrDefault(p => p.Name == "compression")?.Value ?? "";
        dedup.Text = props.FirstOrDefault(p => p.Name == "dedup")?.Value ?? "";
        sync.Text = props.FirstOrDefault(p => p.Name == "sync")?.Value ?? "";
    }

    void ShowUserMode()
    {
        string state = UserModeService.State();
        serviceLabel.Text = state == "NotInstalled"
            ? "The user-mode edition (ZfsWinUserMode service) is not installed."
            : $"ZfsWinUserMode service: {state}";
        mounts.Items.Clear();
        foreach (UserModeMount m in UserModeMounts.Machine().List())
            mounts.Items.Add(new ListViewItem([m.Name, m.Source, m.Pool, m.Dataset, m.MountPoint, m.ReadOnly ? "yes" : "no"]) { Tag = m });
    }

    // --- device actions -----------------------------------------------------------

    async Task AddMirrorAsync()
    {
        if (devicePool.SelectedItem is not string pool || deviceTree.SelectedNode?.Tag is not DeviceNode { Role: "data", Vdev.IsLeaf: true } node)
        {
            Info("Select a data disk (a leaf under the pool, not a cache or log) to mirror.");
            return;
        }
        IReadOnlyList<string> inUse;
        try { inUse = await PoolDevicePathsAsync(); }
        catch (AdminException e) { Error("Find devices in use by pools", e); return; }
        using var dialog = new DeviceDialog("Add mirror device", $"The new device becomes a mirror of {node.Vdev.Path ?? node.Vdev.Name}; it must be at least as large.", 1, offerMirror: false, inUse);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        string target = dialog.Devices[0];
        if (Confirm($"Attach {dialog.SelectionSummary} to {node.Vdev.Path ?? node.Vdev.Name} in {pool}?\r\n\r\nEverything on the selected target is overwritten. ZFS then copies the data (resilver)."))
            await RunAsync($"Attach {target}", () => client.AttachMirrorAsync(pool, node.Vdev.Path ?? node.Vdev.Name, target, confirmed: true));
    }

    async Task DetachMirrorAsync()
    {
        if (devicePool.SelectedItem is not string pool || deviceTree.SelectedNode?.Tag is not DeviceNode { Role: "data", Vdev.IsLeaf: true, Parent.Type: "mirror" } node)
        {
            Info("Select a disk inside a mirror.  A disk that is not part of a mirror holds the only copy and cannot be detached.");
            return;
        }
        string device = node.Vdev.Path ?? node.Vdev.Name;
        if (Confirm($"Detach {device} from its mirror in {pool}?\r\n\r\nThe mirror loses one copy.  ZFS refuses if this device holds the only valid copy of some data."))
            await RunAsync($"Detach {device}", () => client.DetachMirrorAsync(pool, device, confirmed: true));
    }

    async Task AddCacheAsync(string role)
    {
        if (devicePool.SelectedItem is not string pool)
            return;
        string what = role == "cache" ? "read cache (L2ARC)" : "write log (SLOG)";
        IReadOnlyList<string> inUse;
        try { inUse = await PoolDevicePathsAsync(); }
        catch (AdminException e) { Error("Find devices in use by pools", e); return; }
        using var dialog = new DeviceDialog($"Add {what}", role == "cache"
            ? "Read cache devices extend the RAM cache with fast storage (SSD).  Several are used independently."
            : "A write log takes synchronous writes until they reach the pool.  Use fast, power-safe storage and mirror it.", 8, offerMirror: role == "log", inUse);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (Confirm($"Add as {what}{(dialog.Mirror ? " (mirrored)" : "")} to {pool}:\r\n  {dialog.SelectionSummary}\r\n\r\nEverything on these devices is overwritten."))
            await RunAsync($"Add {what}", () => client.AddCacheDevicesAsync(pool, role, dialog.Devices, dialog.Mirror, confirmed: true));
    }

    async Task<IReadOnlyList<string>> PoolDevicePathsAsync()
    {
        var paths = new List<string>();
        foreach (PoolInfo pool in await client.PoolsAsync())
        {
            PoolStatusInfo? status = await client.StatusAsync(pool.Name);
            if (status is null)
                continue;
            foreach (VdevInfo vdev in status.Vdevs.Concat(status.Logs).Concat(status.Caches).Concat(status.Spares)
                         .Concat(status.Special).Concat(status.Dedup).SelectMany(Leaves))
                paths.Add(vdev.Path ?? vdev.Name);
        }
        return paths;

        static IEnumerable<VdevInfo> Leaves(VdevInfo vdev) =>
            vdev.IsLeaf ? [vdev] : vdev.Children.SelectMany(Leaves);
    }

    async Task RemoveCacheAsync()
    {
        if (devicePool.SelectedItem is not string pool || deviceTree.SelectedNode?.Tag is not DeviceNode { Role: "cache" or "log" } node)
        {
            Info("Select a read cache or write log device (or a mirrored log).");
            return;
        }
        string device = node.Vdev.Path ?? node.Vdev.Name;
        if (Confirm($"Remove {device} from {pool}?\r\n\r\nPool data is not affected; a write log is flushed to the pool first."))
            await RunAsync($"Remove {device}", () => client.RemoveDeviceAsync(pool, device, confirmed: true));
    }

    void EditMount(UserModeMount? existing)
    {
        using var dialog = new MountDialog(existing);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Mount is null)
            return;
        UserModeMounts.Machine().Save(dialog.Mount);
        ShowUserMode();
        if (UserModeService.State() == "Running" && Confirm("Restart the ZfsWinUserMode service now so the change takes effect?\r\n\r\nRestarting unmounts and exports every user-mode pool first."))
            _ = RunAsync("Restart ZfsWinUserMode", () => UserModeService.ControlAsync("restart"));
    }

    // --- helpers ------------------------------------------------------------

    async Task RunAsync(string what, Func<Task> action)
    {
        UseWaitCursor = true;
        tabs.Enabled = false;
        statusText.Text = what + "…";
        try
        {
            await action();
            statusText.Text = what + ": done.";
        }
        catch (Exception e) when (e is AdminException or InvalidOperationException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        {
            statusText.Text = what + ": failed.";
            Error(what, e);
        }
        finally
        {
            tabs.Enabled = true;
            UseWaitCursor = false;
        }
        await RefreshAllAsync();
    }

    string? SelectedPool() => pools.SelectedItems.Count > 0 ? pools.SelectedItems[0].Tag as string : null;
    string? SelectedDataset() => datasets.SelectedItems.Count > 0 ? datasets.SelectedItems[0].Tag as string : null;
    UserModeMount? SelectedMount() => mounts.SelectedItems.Count > 0 ? mounts.SelectedItems[0].Tag as UserModeMount : null;

    bool Confirm(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    void Info(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

    void Error(string what, Exception e)
    {
        string detail = e is AdminException { Detail: { Length: > 0 } d } ? "\r\n\r\n" + d : "";
        MessageBox.Show(this, $"{what} failed:\r\n\r\n{e.Message}{detail}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    static ListView NewList(params (string Title, int Width)[] columns)
    {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        foreach (var (title, width) in columns)
            list.Columns.Add(title, width);
        return list;
    }

    static ComboBox Choice(IReadOnlyList<string> values)
    {
        var box = new ComboBox { Width = 160, DropDownStyle = ComboBoxStyle.DropDown };
        box.Items.AddRange(values.Select(v => (object)v).ToArray());
        return box;
    }
}
