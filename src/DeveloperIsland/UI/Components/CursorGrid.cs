using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperIsland.UI.Components;

/// <summary>A Grid that lets its owner set the pointer cursor (ProtectedCursor is protected on UIElement).</summary>
public sealed partial class CursorGrid : Grid
{
    public void SetCursor(InputCursor? cursor) => ProtectedCursor = cursor;
}
