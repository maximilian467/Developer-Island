using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperIsland.UI.Settings;

/// <summary>Settings, Modules section: which token figure Claude Code and Codex show.</summary>
public sealed partial class SettingsWindow
{
    private void LoadTokenMetric(AppSettings s)
    {
        (s.TokenMetric == TokenMetric.AllProcessed ? TokenMetricAll : TokenMetricFresh).IsChecked = true;
        TokenMetricFresh.IsEnabled = TokenMetricAll.IsEnabled = s.ClaudeEnabled || s.CodexEnabled;
    }

    private void OnTokenMetricChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<TokenMetric>(tag, out var metric))
        {
            return;
        }

        _store.Update(s => s.TokenMetric = metric);
        Log.Info("settings", "Setting changed", new { setting = "TokenMetric", value = metric.ToString() });
    }
}
