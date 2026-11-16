using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Islet.Core;

/// <summary>Minimal INotifyPropertyChanged base so XAML bindings update automatically.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    public RelayCommand(Action<object?> run) => _run = run;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run(parameter);
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
