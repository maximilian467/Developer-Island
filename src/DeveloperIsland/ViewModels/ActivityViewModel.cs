using Microsoft.UI.Xaml.Media;

namespace DeveloperIsland.ViewModels;

public enum ActivityKind
{
    Music,
    Focus,
    Usage,
    Info,
}

/// <summary>Content of the medium ("live activity") state.</summary>
public sealed class ActivityViewModel : ObservableObject
{
    private ActivityKind _kind = ActivityKind.Info;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _trailing = string.Empty;
    private string _glyph = "";
    private ImageSource? _art;
    private bool _accentGlyph;

    public ActivityKind Kind => _kind;

    public string Title => _title;

    public string Subtitle => _subtitle;

    public string Trailing => _trailing;

    public bool HasTrailing => _trailing.Length > 0;

    public string Glyph => _glyph;

    public ImageSource? Art => _art;

    public bool ShowArt => _art is not null;

    public bool ShowGlyph => _art is null;

    /// <summary>Accent is reserved for a live state (a running focus session).</summary>
    public Brush GlyphBrush => (Brush)Microsoft.UI.Xaml.Application.Current.Resources[_accentGlyph ? "AccentBrush" : "TextPrimaryBrush"];

    public string AccessibleText => string.IsNullOrEmpty(_subtitle) ? _title : $"{_title}, {_subtitle}";

    public void Set(ActivityKind kind, string title, string subtitle, string trailing = "", string glyph = "", ImageSource? art = null, bool accentGlyph = false)
    {
        _kind = kind;
        _title = title;
        _subtitle = subtitle;
        _trailing = trailing;
        _glyph = glyph;
        _art = art;
        _accentGlyph = accentGlyph;
        OnAllPropertiesChanged();
    }

    public void SetArt(ImageSource? art)
    {
        if (!ReferenceEquals(_art, art))
        {
            _art = art;
            OnPropertyChanged(nameof(Art));
            OnPropertyChanged(nameof(ShowArt));
            OnPropertyChanged(nameof(ShowGlyph));
        }
    }

    public void SetTrailing(string trailing)
    {
        if (_trailing != trailing)
        {
            _trailing = trailing;
            OnPropertyChanged(nameof(Trailing));
            OnPropertyChanged(nameof(HasTrailing));
        }
    }
}
