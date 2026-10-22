using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class TasksPanel : UserControl
{
    private TasksViewModel? _viewModel;

    public TasksPanel()
    {
        InitializeComponent();
    }

    public TasksViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            DataContext = value;
        }
    }

    /// <summary>Quick capture: put the cursor in the input.</summary>
    public void FocusCapture() => Dispatcher.UIThread.Post(() => Capture.Focus(), DispatcherPriority.Background);

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            if (ViewModel.Add(Capture.Text ?? string.Empty))
            {
                Capture.Text = string.Empty;
            }

            e.Handled = true;
        }
    }

    private static Guid? IdOf(object? sender) => sender is Control { Tag: Guid id } ? id : null;

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (IdOf(sender) is { } id)
        {
            ViewModel.Toggle(id);
        }
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (IdOf(sender) is { } id)
        {
            ViewModel.Delete(id);
        }
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e)
    {
        if (IdOf(sender) is { } id)
        {
            ViewModel.Move(id, -1);
        }
    }

    private void OnMoveDown(object? sender, RoutedEventArgs e)
    {
        if (IdOf(sender) is { } id)
        {
            ViewModel.Move(id, 1);
        }
    }

    /// <summary>Alt+Up/Down moves the focused task; Delete removes it.</summary>
    private void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (IdOf(sender) is not { } id)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Up or Key.Down)
        {
            ViewModel.Move(id, e.Key == Key.Up ? -1 : 1);
            e.Handled = true;
            Dispatcher.UIThread.Post(() => FocusRow(id), DispatcherPriority.Background);
        }
        else if (e.Key == Key.Delete)
        {
            ViewModel.Delete(id);
            e.Handled = true;
        }
    }

    private void FocusRow(Guid id)
    {
        var check = List.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Tag is Guid g && g == id && !b.Classes.Contains("delete"));
        check?.Focus(NavigationMethod.Tab);
    }

    private void OnClearCompleted(object? sender, RoutedEventArgs e) => ViewModel.ClearCompleted();
}
