using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Blueprint.ControlPanel.Services;

public static class MonitorPicker
{
    /// <summary>
    /// Asks the engine which monitors exist, then shows a dialog letting the
    /// user pick one. Returns the engine's monitor index as a string, or null
    /// if the user cancelled.
    /// </summary>
    public static async Task<string?> ShowAsync(EnginePipeClient client, XamlRoot root)
    {
        var response = await client.SendAsync("list_monitors");
        var mons = response?["body"]?["monitors"] as JsonArray;
        if (mons == null || mons.Count == 0) return null;

        var entries = new List<(int idx, int x, int w, int h, bool primary)>();
        foreach (var m in mons)
        {
            entries.Add((
                m?["index"]?.GetValue<int>() ?? 0,
                m?["x"]?.GetValue<int>() ?? 0,
                m?["width"]?.GetValue<int>() ?? 0,
                m?["height"]?.GetValue<int>() ?? 0,
                m?["primary"]?.GetValue<bool>() ?? false));
        }

        // Sort by X coordinate → left-to-right order.
        entries.Sort((a, b) => a.x.CompareTo(b.x));

        var combo = new ComboBox();
        for (int i = 0; i < entries.Count; i++)
        {
            var (idx, x, w, h, primary) = entries[i];

            string pos;
            if (primary) pos = "Primary";
            else if (i == 0) pos = "Left";
            else if (i == entries.Count - 1) pos = "Right";
            else pos = $"#{i + 1}";

            combo.Items.Add($"Monitor {i + 1} — {pos}  ({w}×{h})");
        }
        combo.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            Title = "Which monitor?",
            Content = combo,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;

        // Return the engine's original index of the picked monitor.
        return entries[combo.SelectedIndex].idx.ToString();
    }
}