using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace TestFlow;

public partial class MainWindow : Window
{
    CancellationTokenSource? _cts;
    FlowWindow? _flow;
    ObservableCollection<TestCase> _cases = AppState.Cases;
    FlowNode? _node;

    public MainWindow()
    {
        InitializeComponent();
        TestCase.StatusChanged += RefreshStats;
        Bind(AppState.Cases, null);
    }

    /// <summary>Show the test cases of one flow node (or the default list when node is null).</summary>
    public void Bind(ObservableCollection<TestCase> cases, FlowNode? node)
    {
        _cases.CollectionChanged -= OnChanged;
        _cases = cases; _node = node;
        _cases.CollectionChanged += OnChanged;
        CasesGrid.ItemsSource = _cases;
        Title = node == null ? "TestFlow – Test Runner"
              : $"TestFlow – Test Runner – {node.Name}" + (node.ExcelFile.Length > 0 ? $" ({node.ExcelFile})" : "");
        RefreshStats();
    }

    void OnChanged(object? s, NotifyCollectionChangedEventArgs e) => RefreshStats();

    void RefreshStats()
    {
        TotalRun.Text = _cases.Count.ToString();
        PassRun.Text = _cases.Count(x => x.Status == "Passed").ToString();
        FailRun.Text = _cases.Count(x => x.Status == "Failed").ToString();
        PendRun.Text = _cases.Count(x => x.Status == "Pending" || x.Status == "Running").ToString();
    }

    void Import_Click(object s, RoutedEventArgs e)
    {
        var f = AppState.ImportInto(_cases);   // replaces only the list currently shown
        if (f == null) return;
        if (_node != null) _node.ExcelFile = f;
        Bind(_cases, _node);
        AppState.NotifyImported();
    }

    void Template_Click(object s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = "TestCases_Template.xlsx" };
        if (dlg.ShowDialog() == true) { ExcelIO.SaveTemplate(dlg.FileName); MessageBox.Show("Template saved."); }
    }

    void CsvGen_Click(object s, RoutedEventArgs e)
    {
        var open = new OpenFileDialog { Filter = "CSV / delimited text|*.csv;*.txt;*.dat|All files|*.*", Title = "Choose a GOOD sample CSV" };
        if (open.ShowDialog() != true) return;
        var save = new SaveFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = "TestCases_" + System.IO.Path.GetFileNameWithoutExtension(open.FileName) + ".xlsx" };
        if (save.ShowDialog() != true) return;
        try
        {
            var r = CsvTestGenerator.Generate(open.FileName, save.FileName);
            var msg = $"Read {r.Rows} row(s), {r.Columns} column(s).\nTest cases: {r.CasesPath}\nSpec: {r.SpecPath}\n\nUse Import Excel to load the test cases. Review the spec: rules are learned from the sample.";
            if (r.Warnings.Count > 0) msg += "\n\nWarnings:\n" + string.Join("\n", r.Warnings.Take(5));
            MessageBox.Show(msg, "CSV → Test Cases");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "CSV → Test Cases", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void OpenFlow_Click(object s, RoutedEventArgs e)
    {
        if (_flow == null || !_flow.IsLoaded) { _flow = new FlowWindow(); _flow.Show(); }
        else _flow.Activate();
    }

    async void RunAll_Click(object s, RoutedEventArgs e) => await RunList(_cases.ToList());
    async void RunSelected_Click(object s, RoutedEventArgs e) => await RunList(CasesGrid.SelectedItems.Cast<TestCase>().ToList());
    void Stop_Click(object s, RoutedEventArgs e) => _cts?.Cancel();

    async Task RunList(List<TestCase> items)
    {
        if (items.Count == 0) return;
        _cts = new CancellationTokenSource(); StopBtn.IsEnabled = true;
        RunContext.Clear();
        foreach (var c in items) { c.Status = "Pending"; c.Message = ""; c.Duration = 0; }
        foreach (var c in items)
        {
            if (_cts.IsCancellationRequested) break;
            await Runner.RunAsync(c, _cts.Token);
        }
        StopBtn.IsEnabled = false;
    }

    void Export_Click(object s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "TestReport.csv" };
        if (dlg.ShowDialog() != true) return;
        static string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Id,Name,Type,Status,DurationMs,Message\n");
        foreach (var c in _cases) sb.AppendLine(string.Join(",", c.Id, Q(c.Name), Q(c.Type), c.Status, c.Duration, Q(c.Message)));
        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
    }
}
