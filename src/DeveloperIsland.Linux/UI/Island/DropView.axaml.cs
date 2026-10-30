using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DeveloperIsland.Core.HomeCloud;
using DeveloperIsland.Core.Island;
using DeveloperIsland.UI.Components;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Island;

/// <summary>The island as a Home Cloud drop zone: the cloud zone on the configured side, text beside it.</summary>
public sealed partial class DropView : UserControl
{
    /// <summary>The pill's width; the arm zone above it is narrower.</summary>
    public const double ZoneWidth = 340;

    private const string CloudGlyph = "";
    private const string UploadGlyph = "";
    private const string CheckGlyph = "";
    private const string ErrorGlyph = "";

    private DropViewModel? _viewModel;

    public DropView()
    {
        InitializeComponent();
    }

    public DropViewModel ViewModel
    {
        get => _viewModel!;
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
        var vm = _viewModel!;
        TitleText.Text = vm.Title;
        DetailText.Text = vm.Detail;

        var waiting = vm.Phase is DropPhase.Hover or DropPhase.None;
        var (glyph, brush) = vm.Phase switch
        {
            DropPhase.Uploading => (UploadGlyph, "AccentBrush"),
            DropPhase.Succeeded => (CheckGlyph, "SuccessBrush"),
            DropPhase.Failed => (ErrorGlyph, "CriticalBrush"),
            _ => (CloudGlyph, vm.IsUnreachable ? "TextTertiaryBrush" : "AccentBrush"),
        };
        ZoneIcon.Glyph = glyph;
        ZoneIcon.Foreground = StatusColors.Resource<IBrush>(brush);

        // The ring around the zone invites the drop; it goes once the files are on their way.
        Zone.BorderBrush = StatusColors.Resource<IBrush>(vm.IsUnreachable ? "DividerBrush" : "AccentBrush");
        Zone.BorderThickness = new Thickness(waiting ? 1.5 : 0);

        Bar.IsVisible = vm.Progress is not null;
        Bar.Fraction = vm.Progress ?? 0;
    }
}
