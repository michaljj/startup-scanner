using System;
using System.ComponentModel;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;

namespace StartupScanner.App.ViewModels;

public sealed class ItemViewModel : INotifyPropertyChanged
{
    private readonly Func<ItemViewModel, bool, bool> _requestToggle;
    private StartupItem _item;

    public ItemViewModel(StartupItem item, DiffEventKind? changeKind, Func<ItemViewModel, bool, bool> requestToggle)
    {
        _item = item;
        _requestToggle = requestToggle;
        ChangeKind = changeKind;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public StartupItem Item
    {
        get => _item;
        private set
        {
            _item = value;
            Raise(nameof(Item));
            Raise(nameof(IsEnabled));
            Raise(nameof(Publisher));
            Raise(nameof(StateGlyph));
        }
    }

    public DiffEventKind? ChangeKind { get; }

    public string Name => _item.Name;
    public string Location => _item.Location;
    public StartupCategory Category => _item.Category;
    public RiskTier Risk => _item.Risk;

    public bool IsNew => ChangeKind == DiffEventKind.Added;
    public bool IsBlocked => Guardrails.Evaluate(_item).Blocked;
    public string? BlockReason => Guardrails.Evaluate(_item).Reason;

    public string Publisher => _item.File?.Signature?.SignerName
        ?? _item.File?.Version?.Company
        ?? (_item.File is null ? "…" : "—unsigned—");

    /// <summary>Risk colour dot: green low, amber medium, red high or critical.</summary>
    public string StateGlyph => _item.Risk switch
    {
        RiskTier.Low => "\U0001F7E2",
        RiskTier.Medium => "\U0001F7E1",
        _ => "\U0001F534",
    };

    public string ChangeBadge => ChangeKind switch
    {
        DiffEventKind.Added => "⊕",
        DiffEventKind.Removed => "⊖",
        DiffEventKind.Modified => "✎",
        DiffEventKind.StateChanged => "↻",
        _ => "",
    };

    /// <summary>
    /// The checkbox writes immediately. The setter asks the view model to perform the toggle and
    /// only accepts the new value if the write actually landed.
    /// </summary>
    public bool IsEnabled
    {
        get => _item.State == EnableState.Enabled;
        set
        {
            if (value == IsEnabled) return;

            if (!_requestToggle(this, value)) Raise(nameof(IsEnabled));   // snap the checkbox back
        }
    }

    public void Replace(StartupItem item) => Item = item;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
