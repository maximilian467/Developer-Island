using Avalonia.Controls;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.UI.Animations;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Island;

public sealed partial class CompactView : UserControl
{
    private IslandViewModel? _viewModel;

    public CompactView()
    {
        InitializeComponent();
    }

    public IslandViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            DataContext = value;
            value.CompactModuleChanged += OnFeaturedChanged;
            ShowFeaturedIcon();
        }
    }

    /// <summary>A different module is featured: swap its icon and cross-fade the content.</summary>
    private void OnFeaturedChanged()
    {
        ShowFeaturedIcon();
        if (_viewModel!.IsFeaturing && IsLoaded)
        {
            Motion.FadeIn(Featured, delay: TimeSpan.Zero, fromY: 0, fromScale: 0.98);
        }
    }

    private void ShowFeaturedIcon()
    {
        if (_viewModel?.CompactModule is { } module)
        {
            FeaturedIcon.Module = module;
            FeaturedIcon.Foreground = module is ModuleId.Claude or ModuleId.Codex
                ? Components.StatusColors.Resource<Avalonia.Media.IBrush>("TextPrimaryBrush")
                : Components.StatusColors.Resource<Avalonia.Media.IBrush>("TextSecondaryBrush");
        }
    }
}
