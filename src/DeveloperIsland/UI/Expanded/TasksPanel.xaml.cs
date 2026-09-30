using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class TasksPanel : UserControl
{
    private TasksViewModel _viewModel = null!;

    public TasksPanel()
    {
        InitializeComponent();
    }

    public TasksViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            StateView.ViewModel = value;
        }
    }

    /// <summary>Quick capture: put the cursor in the input.</summary>
    public void FocusCapture() => Capture.Focus(FocusState.Programmatic);

    private void OnCaptureKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            if (ViewModel.Add(Capture.Text))
            {
                Capture.Text = string.Empty;
            }

            e.Handled = true;
        }
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            ViewModel.Toggle(id);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            ViewModel.Delete(id);
        }
    }

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            ViewModel.Move(id, -1);
        }
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            ViewModel.Move(id, 1);
        }
    }

    /// <summary>Alt+Up/Down moves the focused task; Delete removes it.</summary>
    private void OnRowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id } element)
        {
            return;
        }

        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (alt && e.Key is VirtualKey.Up or VirtualKey.Down)
        {
            ViewModel.Move(id, e.Key == VirtualKey.Up ? -1 : 1);
            e.Handled = true;
            DispatcherQueue.TryEnqueue(() => FocusRow(id));
        }
        else if (e.Key == VirtualKey.Delete)
        {
            ViewModel.Delete(id);
            e.Handled = true;
        }
    }

    private void FocusRow(Guid id)
    {
        var index = ViewModel.Items.ToList().FindIndex(t => t.Id == id);
        if (index >= 0 && List.ContainerFromIndex(index) is ContentPresenter { Content: not null } presenter
            && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(presenter, 0) is Grid row && row.Children[0] is Button check)
        {
            check.Focus(FocusState.Keyboard);
        }
    }

    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e) => SetDeleteVisible(sender, true);

    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e) => SetDeleteVisible(sender, false);

    private static void SetDeleteVisible(object sender, bool visible)
    {
        if (sender is Grid { Children.Count: 3 } row)
        {
            row.Children[2].Opacity = visible ? 1 : 0;
        }
    }

    private void OnClearCompleted(object sender, RoutedEventArgs e) => ViewModel.ClearCompleted();
}
