using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using StartupScanner.Core;
using StartupScanner.Core.Diff;
using StartupScanner.Core.Models;
using StartupScanner.Core.Presentation;

namespace StartupScanner.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ScannerHost _host;
    private IReadOnlyList<StartupItem> _scanned = Array.Empty<StartupItem>();
    private IReadOnlyList<DiffEvent> _events = Array.Empty<DiffEvent>();
    private DateTime? _previousScanUtc;
    private string _statusText = "Ready.";
    private bool _isScanning;
    private string _enrichmentProgress = "";
    private ItemViewModel? _selectedItem;
    private string _searchText = "";
    private bool _changesOnly;
    private bool _hideMicrosoftSigned;

    public MainViewModel(ScannerHost host)
    {
        _host = host;
        Details = new DetailsViewModel(host);
        RescanCommand = new RelayCommand(() => _ = RescanAsync(), () => !IsScanning);
        ExportCommand = new RelayCommand(Export, () => Items.Count > 0);
        UndoLastCommand = new RelayCommand(UndoLast, () => _host.Journal.All.Count > 0);
        UndoSessionCommand = new RelayCommand(UndoSession, () => _host.Journal.ForSession(_host.SessionId).Count > 0);
        DismissErrorsCommand = new RelayCommand(() => Errors.Clear());
        Errors.CollectionChanged += (_, _) => Raise(nameof(HasErrors));

        foreach (var category in Enum.GetValues<StartupCategory>())
            CategoryToggles.Add(new CategoryToggleViewModel(category, ApplyFilter));
    }

    public DetailsViewModel Details { get; private set; } = null!;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ItemViewModel> Items { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();
    public RelayCommand RescanCommand { get; }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; Raise(nameof(StatusText)); }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            _isScanning = value;
            Raise(nameof(IsScanning));
            RescanCommand.RaiseCanExecuteChanged();
        }
    }

    public string EnrichmentProgress
    {
        get => _enrichmentProgress;
        private set { _enrichmentProgress = value; Raise(nameof(EnrichmentProgress)); }
    }

    public ItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            _selectedItem = value;
            Details.Show(value?.Item);
            Raise(nameof(SelectedItem));
        }
    }

    /// <summary>Ids that changed since the previous scan; the filter and the badges both use this.</summary>
    public HashSet<string> ChangedIds { get; } = new(StringComparer.Ordinal);

    public async Task RescanAsync()
    {
        IsScanning = true;
        StatusText = "Scanning…";
        Errors.Clear();

        try
        {
            var previous = _host.Snapshots.Load();
            _previousScanUtc = previous?.ScannedAtUtc;

            var context = _host.ContextFor();
            var result = await Task.Run(() => _host.Engine.Run(context));

            var now = DateTime.UtcNow;
            _events = DiffEngine.Compare(previous?.Items ?? Array.Empty<StartupItem>(), result.Items, now);
            _scanned = result.Items;

            ChangedIds.Clear();
            foreach (var e in _events) ChangedIds.Add(e.Id);

            _host.History.Append(_events);
            _host.Snapshots.Save(result.Items, now);

            Rebuild();

            foreach (var error in result.Errors.Select(e => $"{e.ProviderId}: {e.Location} — {e.Message}").Distinct())
                Errors.Add(error);

            StatusText = StatusLine.Format(result.Items.Count, _host.Providers.Count, _events, _previousScanUtc);

            _ = EnrichAsync();
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>Rebuilds the visible rows from the current scan and the current filter.</summary>
    public void Rebuild()
    {
        var byId = _events.GroupBy(e => e.Id, StringComparer.Ordinal)
                          .ToDictionary(g => g.Key, g => g.First().Event, StringComparer.Ordinal);

        Items.Clear();
        foreach (var item in ItemFilter.Apply(_scanned, Filter, ChangedIds))
        {
            byId.TryGetValue(item.Id, out var kind);
            Items.Add(new ItemViewModel(item, byId.ContainsKey(item.Id) ? kind : null, RequestToggle));
        }
    }

    /// <summary>Set by the toolbar; Rebuild() is what applies it.</summary>
    public FilterSettings Filter { get; set; } = new();

    public ObservableCollection<CategoryToggleViewModel> CategoryToggles { get; } = new();
    public RelayCommand ExportCommand { get; private set; } = null!;

    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; Raise(nameof(SearchText)); ApplyFilter(); }
    }

    public bool ChangesOnly
    {
        get => _changesOnly;
        set { _changesOnly = value; Raise(nameof(ChangesOnly)); ApplyFilter(); }
    }

    public bool HideMicrosoftSigned
    {
        get => _hideMicrosoftSigned;
        set { _hideMicrosoftSigned = value; Raise(nameof(HideMicrosoftSigned)); ApplyFilter(); }
    }

    private void ApplyFilter()
    {
        Filter = new FilterSettings
        {
            Search = SearchText,
            Categories = CategoryToggles.Where(c => c.IsSelected).Select(c => c.Category).ToHashSet(),
            ChangesOnly = ChangesOnly,
            HideMicrosoftSigned = HideMicrosoftSigned,
        };
        Rebuild();
    }

    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"startupscanner-{DateTime.Now:yyyyMMdd-HHmm}",
            DefaultExt = ".csv",
            Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json",
        };

        if (dialog.ShowDialog() != true) return;

        var rows = Items.Select(i => i.Item).ToList();
        var text = dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? Exporter.ToJson(rows)
            : Exporter.ToCsv(rows);

        System.IO.File.WriteAllText(dialog.FileName, text);
        StatusText = $"Exported {rows.Count} rows to {dialog.FileName}";
    }

    public RelayCommand UndoLastCommand { get; private set; } = null!;
    public RelayCommand UndoSessionCommand { get; private set; } = null!;
    public bool HasErrors => Errors.Count > 0;
    public RelayCommand DismissErrorsCommand { get; private set; } = null!;

    /// <summary>
    /// Returns true only when the write landed. A false return makes the checkbox snap back, so the
    /// UI never shows a state the read-back rejected.
    /// </summary>
    private bool RequestToggle(ItemViewModel row, bool enable)
    {
        var result = _host.ToggleService.SetEnabled(row.Item, enable, _host.ContextFor(row.Item));

        if (result.Success)
        {
            row.Replace(row.Item with
            {
                State = enable ? EnableState.Enabled : EnableState.Disabled,
                StateSource = result.Record!.Mechanism,
            });
            StatusText = $"{(enable ? "Enabled" : "Disabled")} {row.Name}.";
            UndoLastCommand.RaiseCanExecuteChanged();
            UndoSessionCommand.RaiseCanExecuteChanged();
            return true;
        }

        if (result.Cancelled)
        {
            if (result.Error is not null) StatusText = result.Error;
            return false;
        }

        Errors.Add($"{row.Name}: {result.Error}");
        StatusText = $"Could not change {row.Name}.";
        return false;
    }

    private void UndoLast()
    {
        var result = _host.Undo.UndoLast(_host.ContextFor());
        StatusText = result.Success ? "Undid the last change." : result.Error ?? "Nothing to undo.";
        if (!result.Success && result.Error is not null && !result.Cancelled) Errors.Add(result.Error);
        _ = RescanAsync();
    }

    private void UndoSession()
    {
        var results = _host.Undo.UndoSession(_host.ContextFor());
        var undone = results.Count(r => r.Success);

        StatusText = results.Count == 0
            ? "No changes from this session to undo."
            : $"Undid {undone} of {results.Count} changes from this session.";

        foreach (var failure in results.Where(r => !r.Success && r.Error is not null))
            Errors.Add(failure.Error!);

        _ = RescanAsync();
    }

    /// <summary>Phase 2: hash, PE version, and Authenticode in the background.</summary>
    private async Task EnrichAsync()
    {
        var paths = _scanned
            .Where(i => !string.IsNullOrWhiteSpace(i.ImagePath))
            .Select(i => i.ImagePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var done = 0;

        await Task.Run(() => Parallel.ForEach(paths, path =>
        {
            var facts = _host.Files.Get(path);
            var completed = Interlocked.Increment(ref done);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var row in Items.Where(r =>
                             string.Equals(r.Item.ImagePath, path, StringComparison.OrdinalIgnoreCase)))
                    row.Replace(row.Item with { File = facts });

                EnrichmentProgress = completed < paths.Count ? $"Analysing files… {completed}/{paths.Count}" : "";
            });
        }));

        // Keep the enriched facts on the backing list so a filter rebuild does not lose them.
        _scanned = _scanned
            .Select(i => i.ImagePath is null ? i : i with { File = _host.Files.Get(i.ImagePath) })
            .ToList();
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
