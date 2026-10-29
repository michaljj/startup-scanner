using System;
using System.ComponentModel;
using StartupScanner.Core.Models;

namespace StartupScanner.App.ViewModels;

public sealed class CategoryToggleViewModel(StartupCategory category, Action onChanged) : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public StartupCategory Category { get; } = category;
    public string Label { get; } = category.ToString();

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            onChanged();
        }
    }
}
