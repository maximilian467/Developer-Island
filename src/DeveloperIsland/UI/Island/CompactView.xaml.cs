using DeveloperIsland.Core.Modules;
using DeveloperIsland.UI.Animations;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperIsland.UI.Island;

public sealed partial class CompactView : UserControl
{
    private IslandViewModel _viewModel = null!;

    public CompactView()
    {
        InitializeComponent();
    }

    public IslandViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            value.CompactModuleChanged += OnFeaturedChanged;
            ShowFeaturedIcon();
        }
    }

    /// <summary>A different module is featured: swap its icon and cross-fade the content.</summary>
    private void OnFeaturedChanged()
    {
        ShowFeaturedIcon();
        if (_viewModel.IsFeaturing && IsLoaded)
        {
            Motion.FadeIn(Featured, delay: TimeSpan.Zero, fromY: 0, fromScale: 0.98f);
        }
    }

    private void ShowFeaturedIcon()
    {
        var module = _viewModel.CompactModule;
        var isProvider = module is ModuleId.Claude or ModuleId.Codex;
        FeaturedMark.Visibility = isProvider ? Visibility.Visible : Visibility.Collapsed;
        FeaturedModuleIcon.Visibility = isProvider ? Visibility.Collapsed : Visibility.Visible;
        if (isProvider)
        {
            FeaturedMark.Kind = module == ModuleId.Claude ? "Claude" : "Codex";
        }
        else if (module is { } m)
        {
            FeaturedModuleIcon.Module = m;
        }
    }
}
