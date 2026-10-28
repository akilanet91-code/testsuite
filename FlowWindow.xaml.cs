using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace TestFlow;

public partial class FlowWindow : Window
{
    class NodeView { public Border Box = null!; public TextBlock Title = null!, Name = null!, Sum = null!, Desc = null!, Icon = null!, Check = null!; }

    FlowDoc doc = new();
    readonly Dictionary<int, NodeView> views = new();
    FlowNode? sel, linkFrom, drag;
    Point dragOff, dragStart; bool moved;
    CancellationTokenSource? cts;
    int nextId = 1;

    Brush Res(string k) => (Brush)FindResource(k);
    static Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public FlowWindow()
    {
        InitializeComponent();
        foreach (var (kind, info) in Kinds.All)
        {
            var k = kind;
            var b = new Button { Content = $"{info.Icon}  {info.Title}", HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
            b.Click += (_, _) => PaletteAdd(k);
            Palette.Children.Add(b);
        }
        var a = AddNode("CtrlMJob", 40, 40);
        var c = AddNode("ApiCall", 330, 40);
        // no default connection: link nodes manually (right dot -> left dot)
        DrawEdges();
        RefreshAll();
        Log("INFO", "Ready. Click a node to edit it; use Advanced > Import Excel to give that node its own test cases.");
        AppState.CasesImported += OnImported;
        Closed += (_, _) => AppState.CasesImported -= OnImported;
    }

    void OnImported() => RefreshAll();

    /// <summary>Show this node's test scenarios in the Test Runner window.</summary>
    void OpenRunner(FlowNode n)
    {
        if (Application.Current.MainWindow is not MainWindow mw) return;
        if (!mw.IsVisible) mw.Show();
        if (mw.WindowState == WindowState.Minimized) mw.WindowState = WindowState.Normal;
        mw.Bind(n.Cases, n);
        mw.Topmost = true; mw.Topmost = false;   // bring to front without stealing focus
    }

    void ImportForNode(FlowNode n)
    {
        var file = AppState.ImportInto(n.Cases);   // only this node's cases are replaced
        if (file == null) return;
        n.ExcelFile = file;
        Log("INFO", $"“{n.Name}”: imported {n.Cases.Count} test cases from {file}");
        Refresh(n); OpenRunner(n); AppState.NotifyImported();
    }

    // ---------- nodes ----------
    void PaletteAdd(string kind)
    {
        int n = doc.Nodes.Count;
        var node = AddNode(kind, 40 + (n % 4) * 290, 40 + (n / 4) * 160);
        DrawEdges(); Select(node);
    }

    FlowNode AddNode(string kind, double x, double y)
    {
        var n = new FlowNode { Id = nextId++, Kind = kind, Name = Kinds.All[kind].Title, Description = Kinds.DefaultDesc(kind), X = x, Y = y };
        foreach (var f in Kinds.All[kind].Fields) n.Props[f] = Kinds.Default(f);
        doc.Nodes.Add(n); BuildView(n); return n;
    }

    Ellipse Port(string tag, HorizontalAlignment ha) => new()
    {
        Width = 14, Height = 14, Fill = Res("Panel"), Stroke = Res("Mu"), StrokeThickness = 2, Tag = tag,
        HorizontalAlignment = ha, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Cross,
        Margin = ha == HorizontalAlignment.Left ? new Thickness(-9, 0, 0, 0) : new Thickness(0, 0, -9, 0)
    };

    const double NodeW = 250, NodeH = 140;

    void BuildView(FlowNode n)
    {
        var v = new NodeView
        {
            Icon = new TextBlock { FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Name = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
            Title = new TextBlock { FontSize = 10, Foreground = Res("Mu") },
            Check = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
            Desc = new TextBlock { FontSize = 11, Foreground = Res("Mu"), TextTrimming = TextTrimming.CharacterEllipsis },
            Sum = new TextBlock { FontSize = 12, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) }
        };
        var iconBox = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = B("#1E2A55"), Child = v.Icon, Margin = new Thickness(0, 0, 10, 0) };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(v.Name); titles.Children.Add(v.Title);
        var head = new DockPanel { Margin = new Thickness(14, 12, 14, 10) };
        DockPanel.SetDock(iconBox, Dock.Left); DockPanel.SetDock(v.Check, Dock.Right);
        head.Children.Add(iconBox); head.Children.Add(v.Check); head.Children.Add(titles);
        var body = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        body.Children.Add(v.Desc); body.Children.Add(v.Sum);
        var stack = new StackPanel();
        stack.Children.Add(head); stack.Children.Add(new Border { Height = 1, Background = Res("Line") }); stack.Children.Add(body);
        var grid = new Grid();
        grid.Children.Add(stack); grid.Children.Add(Port("in", HorizontalAlignment.Left)); grid.Children.Add(Port("out", HorizontalAlignment.Right));
        v.Box = new Border { Width = NodeW, Height = NodeH, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Background = Res("Card"), Child = grid, Cursor = Cursors.Hand };
        Panel.SetZIndex(v.Box, 5);

        var menu = new ContextMenu();
        var del = new MenuItem { Header = "Delete node" }; del.Click += (_, _) => RemoveNode(n);
        var dis = new MenuItem { Header = "Remove connections" }; dis.Click += (_, _) => { doc.Edges.RemoveAll(e => e.From == n.Id || e.To == n.Id); DrawEdges(); };
        menu.Items.Add(del); menu.Items.Add(dis); v.Box.ContextMenu = menu;

        v.Box.MouseLeftButtonDown += (_, e) => NodeDown(n, e);
        v.Box.MouseMove += (_, e) => NodeMove(n, e);
        v.Box.MouseLeftButtonUp += (_, _) => NodeUp(n);
        Surface.Children.Add(v.Box); views[n.Id] = v; Place(n); Refresh(n);
    }

    void Place(FlowNode n) { var b = views[n.Id].Box; Canvas.SetLeft(b, n.X); Canvas.SetTop(b, n.Y); }

    void Refresh(FlowNode n)
    {
        var v = views[n.Id]; var k = Kinds.All[n.Kind];
        v.Icon.Text = k.Icon; v.Name.Text = n.Name; v.Title.Text = k.Title.ToUpperInvariant();
        v.Desc.Text = string.IsNullOrWhiteSpace(n.Description) ? Kinds.DefaultDesc(n.Kind) : n.Description;
        v.Sum.Text = n.Cases.Count > 0 ? $"📊 {(n.ExcelFile.Length > 0 ? n.ExcelFile : "cases")} · {n.Cases.Count} rows"
            : n.Props.Values.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "—";
        v.Check.Text = n.Status switch { "passed" => "✅", "failed" => "❌", "running" => "⏳", _ => "" };
        v.Box.BorderBrush = n.Status switch
        {
            "passed" => B("#22C55E"), "failed" => B("#EF4444"), "running" => B("#F59E0B"),
            _ => linkFrom == n ? B("#F59E0B") : sel == n ? Res("Ac") : Res("Line")
        };
        if (sel == n) LastRunTxt.Text = n.LastRun.Length == 0 ? "🕒 Last run: not run yet" : "🕒 Last run: " + n.LastRun;
    }
    void RefreshAll() { foreach (var n in doc.Nodes) Refresh(n); UpdateChip(); }

    void UpdateChip()
    {
        FlowInfo.Text = $"{doc.Nodes.Count} nodes";
        if (sel != null && sel.Cases.Count > 0) ChipTxt.Text = $"📊 {sel.ExcelFile} · {sel.Cases.Count} rows";
        else
        {
            int c = doc.Nodes.Count(x => x.Cases.Count > 0);
            ChipTxt.Text = c == 0 ? "📊 No Excel imported" : $"📊 {c} node(s) with Excel";
        }
    }

    void NodeDown(FlowNode n, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Ellipse el)
        {
            if ((string)el.Tag == "out") linkFrom = linkFrom == n ? null : n;
            else if (linkFrom != null && linkFrom != n)
            {
                if (!doc.Edges.Any(x => x.From == linkFrom.Id && x.To == n.Id)) doc.Edges.Add(new FlowEdge { From = linkFrom.Id, To = n.Id });
                linkFrom = null; DrawEdges();
            }
            RefreshAll(); e.Handled = true; return;
        }
        drag = n; moved = false; dragStart = e.GetPosition(Surface);
        dragOff = new Point(dragStart.X - n.X, dragStart.Y - n.Y);
        views[n.Id].Box.CaptureMouse(); e.Handled = true;
    }
    void NodeMove(FlowNode n, MouseEventArgs e)
    {
        if (drag != n) return;
        var p = e.GetPosition(Surface);
        if ((p - dragStart).Length > 3) moved = true;
        if (!moved) return;
        n.X = Math.Max(0, p.X - dragOff.X); n.Y = Math.Max(0, p.Y - dragOff.Y);
        Place(n); DrawEdges();
    }
    void NodeUp(FlowNode n)
    {
        if (drag != n) return;
        views[n.Id].Box.ReleaseMouseCapture(); drag = null;
        if (!moved) Select(n);
    }
    void Surface_Down(object s, MouseButtonEventArgs e) { linkFrom = null; CloseSide(); }

    void DrawEdges()
    {
        foreach (var p in Surface.Children.OfType<System.Windows.Shapes.Path>().ToList()) Surface.Children.Remove(p);
        foreach (var ed in doc.Edges)
        {
            var a = doc.Nodes.FirstOrDefault(n => n.Id == ed.From); var b = doc.Nodes.FirstOrDefault(n => n.Id == ed.To);
            if (a == null || b == null) continue;
            Point p1 = new(a.X + NodeW, a.Y + NodeH / 2), p2 = new(b.X, b.Y + NodeH / 2);
            double dx = Math.Max(50, Math.Abs(p2.X - p1.X) / 2);
            var f = new PathFigure { StartPoint = p1 };
            f.Segments.Add(new BezierSegment(new Point(p1.X + dx, p1.Y), new Point(p2.X - dx, p2.Y), p2, true));
            Surface.Children.Insert(0, new System.Windows.Shapes.Path
            { Data = new PathGeometry(new[] { f }), Stroke = Res("Mu"), StrokeThickness = 2, IsHitTestVisible = false });
        }
    }

    void RemoveNode(FlowNode n)
    {
        doc.Edges.RemoveAll(e => e.From == n.Id || e.To == n.Id);
        Surface.Children.Remove(views[n.Id].Box); views.Remove(n.Id); doc.Nodes.Remove(n);
        if (sel == n) CloseSide();
        DrawEdges();
    }

    // ---------- settings panel ----------
    void Select(FlowNode n) { sel = n; Side.Visibility = Visibility.Visible; BuildSide(n); RefreshAll(); OpenRunner(n); }
    void CloseSide() { sel = null; Side.Visibility = Visibility.Collapsed; RefreshAll(); }
    void CloseSide_Click(object s, RoutedEventArgs e) => CloseSide();
    void DeleteNode_Click(object s, RoutedEventArgs e) { if (sel != null) RemoveNode(sel); }
    void TabP_Click(object s, RoutedEventArgs e) { PropsPanel.Visibility = Visibility.Visible; AdvPanel.Visibility = Visibility.Collapsed; }
    void TabA_Click(object s, RoutedEventArgs e) { PropsPanel.Visibility = Visibility.Collapsed; AdvPanel.Visibility = Visibility.Visible; }

    void AddField(Panel p, string label, string val, Action<string> set, bool multi = false)
    {
        p.Children.Add(new TextBlock { Text = label, Foreground = Res("Mu"), FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
        var tb = new TextBox { Text = val };
        if (multi) { tb.AcceptsReturn = true; tb.TextWrapping = TextWrapping.Wrap; tb.MinHeight = 64; tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
        tb.TextChanged += (_, _) => set(tb.Text);
        p.Children.Add(tb);
    }

    void Section(Panel p, string title) =>
        p.Children.Add(new TextBlock { Text = title, Foreground = Res("Mu"), FontSize = 11, Margin = new Thickness(0, 18, 0, 0) });

    void BuildSide(FlowNode n)
    {
        SideTitle.Text = n.Name; PropsPanel.Children.Clear(); AdvPanel.Children.Clear();
        var k = Kinds.All[n.Kind];

        // action type card
        var card = new Border { CornerRadius = new CornerRadius(10), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), Background = Res("Card"), Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = k.Icon, FontSize = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        var col = new StackPanel();
        col.Children.Add(new TextBlock { Text = "Action type", Foreground = Res("Mu"), FontSize = 11 });
        col.Children.Add(new TextBlock { Text = k.Title.ToUpperInvariant(), FontWeight = FontWeights.SemiBold });
        row.Children.Add(col); card.Child = row; PropsPanel.Children.Add(card);

        AddField(PropsPanel, "Node name", n.Name, t => { n.Name = t; SideTitle.Text = t; Refresh(n); });
        AddField(PropsPanel, "Description", n.Description, t => { n.Description = t; Refresh(n); }, multi: true);
        foreach (var (title, fields) in Kinds.Layout(n.Kind))
        {
            Section(PropsPanel, title);
            foreach (var f in fields)
                AddField(PropsPanel, Kinds.Label(f), n.Props.GetValueOrDefault(f, ""), t => { n.Props[f] = t; Refresh(n); }, multi: f is "DbQuery" or "DbConnectionString" or "ColumnMap" or "FieldLayout");
        }
        if (n.Kind == "TestSuite")
            PropsPanel.Children.Add(new TextBlock { Text = "Runs this node’s own Excel test cases (Advanced > Import Excel); passes only if all pass.", Foreground = Res("Mu"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });

        // advanced
        Section(AdvPanel, "EXECUTION");
        AddField(AdvPanel, "Retry count", n.Retry.ToString(), t => { if (int.TryParse(t, out var v)) n.Retry = v; });
        AddField(AdvPanel, "Retry delay (ms)", n.RetryDelayMs.ToString(), t => { if (int.TryParse(t, out var v)) n.RetryDelayMs = v; });
        AddField(AdvPanel, "Timeout (ms)", n.TimeoutMs.ToString(), t => { if (int.TryParse(t, out var v)) n.TimeoutMs = v; });
        AddField(AdvPanel, "Run condition (e.g. previous passed / previous failed)", n.RunCondition, t => n.RunCondition = t);
        Section(AdvPanel, "ERROR HANDLING");
        var cb = new ComboBox { ItemsSource = new[] { "Stop flow", "Continue" }, SelectedItem = n.OnFailure, Margin = new Thickness(0, 4, 0, 0) };
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s2) n.OnFailure = s2; };
        AdvPanel.Children.Add(cb);
        Section(AdvPanel, "TEST CASES (this node only)");
        var info = new TextBlock { Foreground = Res("Mu"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6),
            Text = n.Cases.Count > 0 ? $"{n.ExcelFile} – {n.Cases.Count} rows" : "No Excel imported yet" };
        AdvPanel.Children.Add(info);
        var imp = new Button { Content = "⬆ Import Excel test cases…", Margin = new Thickness(0) };
        imp.Click += (_, _) => { ImportForNode(n); info.Text = n.Cases.Count > 0 ? $"{n.ExcelFile} – {n.Cases.Count} rows" : "No Excel imported yet"; };
        AdvPanel.Children.Add(imp);
        var show = new Button { Content = "📊 Show in Test Runner", Margin = new Thickness(0, 8, 0, 0) };
        show.Click += (_, _) => OpenRunner(n);
        AdvPanel.Children.Add(show);
    }

    // ---------- Excel / save / load ----------
    void Import_Click(object s, RoutedEventArgs e)
    {
        if (sel == null) { MessageBox.Show("Click a node first. Each node keeps its own Excel test cases."); return; }
        ImportForNode(sel);
    }

    void Save_Click(object s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "TestFlow flow (*.json)|*.json", FileName = "flow.json" };
        if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(doc, Json));
    }
    void Load_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "TestFlow flow (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var d = JsonSerializer.Deserialize<FlowDoc>(File.ReadAllText(dlg.FileName)) ?? new FlowDoc();
            Surface.Children.Clear(); views.Clear(); doc = d; sel = linkFrom = null; Side.Visibility = Visibility.Collapsed;
            nextId = d.Nodes.Count == 0 ? 1 : d.Nodes.Max(n => n.Id) + 1;
            foreach (var n in d.Nodes) BuildView(n);
            DrawEdges();
        }
        catch (Exception ex) { MessageBox.Show("Load failed: " + ex.Message); }
    }

    // ---------- running ----------
    void Log(string level, string msg)
    {
        var tb = new TextBlock
        {
            Text = $"{DateTime.Now:HH:mm:ss}  {level}  {msg}", FontFamily = new FontFamily("Consolas"),
            Foreground = level == "PASS" ? B("#22C55E") : level == "FAIL" ? B("#EF4444") : Res("Mu")
        };
        LogBox.Items.Add(tb); LogBox.ScrollIntoView(tb);
    }

    static TestCase ToTestCase(FlowNode n)
    {
        var tc = new TestCase { Name = n.Name, Type = n.Kind, TimeoutSeconds = Math.Max(1, n.TimeoutMs / 1000) };
        foreach (var kv in n.Props)
        {
            var p = typeof(TestCase).GetProperty(kv.Key);
            if (p == null || string.IsNullOrEmpty(kv.Value)) continue;
            if (p.PropertyType == typeof(int)) p.SetValue(tc, int.TryParse(kv.Value, out var i) ? i : 0);
            else if (p.PropertyType == typeof(string)) p.SetValue(tc, kv.Value);
        }
        return tc;
    }

    async Task<bool> ExecNodeAsync(FlowNode n, CancellationToken ct)
    {
        n.Status = "running"; Refresh(n); Log("INFO", $"Running “{n.Name}”");
        bool ok = false;
        if (n.Cases.Count > 0)
        {
            foreach (var c in n.Cases.ToList())
            {
                ct.ThrowIfCancellationRequested();
                c.Status = "Pending"; c.Message = ""; c.Duration = 0;
                await Runner.RunAsync(c, ct);
            }
            int pass = n.Cases.Count(c => c.Status == "Passed");
            ok = pass == n.Cases.Count;
            Log(ok ? "PASS" : "FAIL", $"{pass}/{n.Cases.Count} test cases passed");
        }
        else if (n.Kind == "TestSuite")
        {
            Log("FAIL", "No Excel test cases imported on this node");
        }
        else
        {
            int attempts = Math.Max(0, n.Retry) + 1;
            for (int i = 1; i <= attempts && !ok; i++)
            {
                var tc = ToTestCase(n);
                await Runner.RunAsync(tc, ct);
                ct.ThrowIfCancellationRequested();
                ok = tc.Status == "Passed";
                Log(ok ? "INFO" : "FAIL", $"Attempt {i}/{attempts}: {tc.Message}");
                if (!ok && i < attempts) await Task.Delay(n.RetryDelayMs, ct);
            }
        }
        n.Status = ok ? "passed" : "failed"; n.LastRun = $"{n.Status} · {DateTime.Now:HH:mm:ss}"; Refresh(n);
        Log(ok ? "PASS" : "FAIL", $"Node “{n.Name}” {(ok ? "completed successfully" : "failed")}");
        return ok;
    }

    List<FlowNode> Order()
    {
        var incoming = doc.Edges.Select(e => e.To).ToHashSet();
        var queue = new Queue<FlowNode>(doc.Nodes.Where(n => !incoming.Contains(n.Id)));
        var res = new List<FlowNode>(); var seen = new HashSet<int>();
        while (queue.Count > 0)
        {
            var n = queue.Dequeue(); if (!seen.Add(n.Id)) continue; res.Add(n);
            foreach (var e in doc.Edges.Where(e => e.From == n.Id)) { var t = doc.Nodes.FirstOrDefault(x => x.Id == e.To); if (t != null) queue.Enqueue(t); }
        }
        res.AddRange(doc.Nodes.Where(n => !seen.Contains(n.Id)));
        return res;
    }

    static bool ShouldRun(FlowNode n, string prev)
    {
        var c = n.RunCondition.ToLowerInvariant();
        if (c.Contains("failed")) return prev == "failed";
        if (c.Contains("passed")) return prev == "passed";
        return true;
    }

    async void RunFlow_Click(object s, RoutedEventArgs e)
    {
        if (doc.Nodes.Count == 0) return;
        cts = new CancellationTokenSource(); StopBtn.IsEnabled = true;
        foreach (var n in doc.Nodes) n.Status = "";
        RefreshAll(); Log("INFO", $"Flow started: {doc.Nodes.Count} nodes");
        string prev = "passed";
        try
        {
            foreach (var n in Order())
            {
                if (!ShouldRun(n, prev)) { Log("INFO", $"Skipped “{n.Name}” (run condition)"); continue; }
                bool ok = await ExecNodeAsync(n, cts.Token);
                prev = ok ? "passed" : "failed";
                if (!ok && n.OnFailure == "Stop flow") { Log("FAIL", "Flow stopped"); return; }
            }
            Log("PASS", "Flow finished");
        }
        catch (OperationCanceledException) { Log("INFO", "Flow stopped by user"); }
        finally { StopBtn.IsEnabled = false; }
    }

    async void RunNode_Click(object s, RoutedEventArgs e)
    {
        if (sel == null) return;
        cts = new CancellationTokenSource(); StopBtn.IsEnabled = true;
        try { await ExecNodeAsync(sel, cts.Token); }
        catch (OperationCanceledException) { Log("INFO", "Stopped by user"); }
        finally { StopBtn.IsEnabled = false; }
    }

    void Stop_Click(object s, RoutedEventArgs e) => cts?.Cancel();
}
