using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LiteClicker.App.ViewModels;

public sealed class ActionCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
public sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> onError) : ICommand
{
    private bool executing;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !executing && canExecute();
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        executing = true; Refresh();
        try { await execute(); }
        catch (Exception ex) { onError(ex); }
        finally { executing = false; Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
