using System;
using System.Windows;
using System.Windows.Input;

namespace UniShare.UI.ViewModels
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> m_execute;
        private readonly Predicate<object?>? m_canExecute;
        private event EventHandler? m_internalCanExecuteChanged;

        public event EventHandler? CanExecuteChanged
        {
            add
            {
                m_internalCanExecuteChanged += value;
                CommandManager.RequerySuggested += value;
            }
            remove
            {
                m_internalCanExecuteChanged -= value;
                CommandManager.RequerySuggested -= value;
            }
        }

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            m_execute = execute ?? throw new ArgumentNullException(nameof(execute));
            m_canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute != null ? _ => canExecute() : null)
        {
        }

        public bool CanExecute(object? parameter) => m_canExecute == null || m_canExecute(parameter);

        public void Execute(object? parameter) => m_execute(parameter);

        public void RaiseCanExecuteChanged()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.InvokeAsync(() =>
                {
                    m_internalCanExecuteChanged?.Invoke(this, EventArgs.Empty);
                    CommandManager.InvalidateRequerySuggested();
                });
            }
            else
            {
                m_internalCanExecuteChanged?.Invoke(this, EventArgs.Empty);
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
