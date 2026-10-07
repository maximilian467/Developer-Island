using DeveloperIsland.Core.HomeCloud;
using DeveloperIsland.Core.Island;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeveloperIsland.UI.Island;

/// <summary>The island as a Home Cloud drop zone: the cloud zone on the configured side, text beside it.</summary>
public sealed partial class DropView : UserControl
{
    /// <summary>The pill's width (see DropView.xaml); the arm zone above it is narrower.</summary>
    public const double ZoneWidth = 340;

    private const string CloudGlyph = "";
    private const string UploadGlyph = "";
    private const string CheckGlyph = "";
    private const string ErrorGlyph = "";

    private DropViewModel _viewModel = null!;

    public DropView()
    {
        InitializeComponent();
    }

    public DropViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            value.PropertyChanged += (_, _) => Render();
            Render();
        }
    }

    public void SetSide(HomeCloudDropSide side)
    {
        var left = side == HomeCloudDropSide.Left;
        Grid.SetColumn(Zone, left ? 0 : 2);
        Texts.Margin = left ? new Thickness(14, 0, 12, 0) : new Thickness(12, 0, 14, 0);
    }

    private void Render()
    {
        var vm = _viewModel;
        TitleText.Text = vm.Title;
        DetailText.Text = vm.Detail;
        AutomationProperties.SetName(Root, vm.AccessibleText);

        var waiting = vm.Phase is DropPhase.Hover or DropPhase.None;
        var (glyph, brush) = vm.Phase switch
        {
            DropPhase.Uploading => (UploadGlyph, "AccentBrush"),
            DropPhase.Succeeded => (CheckGlyph, "SuccessBrush"),
            DropPhase.Failed => (ErrorGlyph, "CriticalBrush"),
            _ => (CloudGlyph, vm.IsUnreachable ? "TextTertiaryBrush" : "AccentBrush"),
        };
        ZoneIcon.Glyph = glyph;
        ZoneIcon.Foreground = Resource(brush);

        // The ring around the zone invites the drop; it goes once the files are on their way.
        Zone.BorderBrush = Resource(vm.IsUnreachable ? "DividerBrush" : "AccentBrush");
        Zone.BorderThickness = new Thickness(waiting ? 1.5 : 0);

        if (vm.Progress is { } progress)
        {
            Bar.Visibility = Visibility.Visible;
            BarDone.Width = new GridLength(progress, GridUnitType.Star);
            BarRest.Width = new GridLength(1 - progress, GridUnitType.Star);
        }
        else
        {
            Bar.Visibility = Visibility.Collapsed;
        }
    }

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}
