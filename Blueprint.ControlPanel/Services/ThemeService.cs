using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;

namespace Blueprint.ControlPanel.Services;

public sealed class AccentScheme
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string BaseTheme { get; init; } = ""; // "Aura", "Catppuccin", etc.
    public Color Accent { get; init; }
    public Color AccentHover { get; init; }
    public Color AccentPressed { get; init; }
}

public static class ThemeService
{
    public static readonly IReadOnlyList<AccentScheme> Schemes = new List<AccentScheme>
    {
        // ── AURA PALETTE (9 Colors) ──
        new() { Id = "aura_purple", BaseTheme = "Aura", DisplayName = "Purple",
                Accent = Color.FromArgb(255, 162, 119, 255), AccentHover = Color.FromArgb(255, 181, 138, 255), AccentPressed = Color.FromArgb(255, 143, 90, 255) },
        new() { Id = "aura_green", BaseTheme = "Aura", DisplayName = "Mint",
                Accent = Color.FromArgb(255, 97, 255, 202), AccentHover = Color.FromArgb(255, 122, 255, 213), AccentPressed = Color.FromArgb(255, 72, 224, 181) },
        new() { Id = "aura_orange", BaseTheme = "Aura", DisplayName = "Gold",
                Accent = Color.FromArgb(255, 255, 202, 133), AccentHover = Color.FromArgb(255, 255, 217, 163), AccentPressed = Color.FromArgb(255, 240, 176, 96) },
        new() { Id = "aura_pink", BaseTheme = "Aura", DisplayName = "Pink",
                Accent = Color.FromArgb(255, 246, 148, 255), AccentHover = Color.FromArgb(255, 251, 176, 255), AccentPressed = Color.FromArgb(255, 224, 112, 240) },
        new() { Id = "aura_blue", BaseTheme = "Aura", DisplayName = "Blue",
                Accent = Color.FromArgb(255, 130, 226, 255), AccentHover = Color.FromArgb(255, 160, 235, 255), AccentPressed = Color.FromArgb(255, 96, 208, 240) },
        new() { Id = "aura_red", BaseTheme = "Aura", DisplayName = "Red",
                Accent = Color.FromArgb(255, 255, 103, 103), AccentHover = Color.FromArgb(255, 255, 133, 133), AccentPressed = Color.FromArgb(255, 224, 69, 69) },
        new() { Id = "aura_yellow", BaseTheme = "Aura", DisplayName = "Yellow",
                Accent = Color.FromArgb(255, 255, 215, 0), AccentHover = Color.FromArgb(255, 255, 228, 77), AccentPressed = Color.FromArgb(255, 224, 181, 0) },
        new() { Id = "aura_teal", BaseTheme = "Aura", DisplayName = "Teal",
                Accent = Color.FromArgb(255, 45, 212, 191), AccentHover = Color.FromArgb(255, 94, 234, 212), AccentPressed = Color.FromArgb(255, 20, 184, 166) },
        new() { Id = "aura_lime", BaseTheme = "Aura", DisplayName = "Lime",
                Accent = Color.FromArgb(255, 163, 230, 53), AccentHover = Color.FromArgb(255, 190, 242, 100), AccentPressed = Color.FromArgb(255, 132, 204, 22) },

        // ── CATPPUCCIN PALETTE (9 Colors - Mocha) ──
        new() { Id = "cat_mauve", BaseTheme = "Catppuccin", DisplayName = "Mauve",
                Accent = Color.FromArgb(255, 203, 166, 247), AccentHover = Color.FromArgb(255, 219, 184, 255), AccentPressed = Color.FromArgb(255, 184, 144, 224) },
        new() { Id = "cat_pink", BaseTheme = "Catppuccin", DisplayName = "Pink",
                Accent = Color.FromArgb(255, 245, 194, 231), AccentHover = Color.FromArgb(255, 250, 212, 240), AccentPressed = Color.FromArgb(255, 224, 160, 208) },
        new() { Id = "cat_flamingo", BaseTheme = "Catppuccin", DisplayName = "Flamingo",
                Accent = Color.FromArgb(255, 242, 205, 205), AccentHover = Color.FromArgb(255, 250, 224, 224), AccentPressed = Color.FromArgb(255, 224, 176, 176) },
        new() { Id = "cat_peach", BaseTheme = "Catppuccin", DisplayName = "Peach",
                Accent = Color.FromArgb(255, 250, 179, 135), AccentHover = Color.FromArgb(255, 255, 201, 163), AccentPressed = Color.FromArgb(255, 224, 155, 106) },
        new() { Id = "cat_yellow", BaseTheme = "Catppuccin", DisplayName = "Yellow",
                Accent = Color.FromArgb(255, 249, 226, 175), AccentHover = Color.FromArgb(255, 255, 239, 201), AccentPressed = Color.FromArgb(255, 224, 200, 128) },
        new() { Id = "cat_green", BaseTheme = "Catppuccin", DisplayName = "Green",
                Accent = Color.FromArgb(255, 166, 227, 161), AccentHover = Color.FromArgb(255, 194, 240, 191), AccentPressed = Color.FromArgb(255, 140, 200, 136) },
        new() { Id = "cat_teal", BaseTheme = "Catppuccin", DisplayName = "Teal",
                Accent = Color.FromArgb(255, 148, 226, 213), AccentHover = Color.FromArgb(255, 176, 240, 229), AccentPressed = Color.FromArgb(255, 112, 200, 184) },
        new() { Id = "cat_sky", BaseTheme = "Catppuccin", DisplayName = "Sky",
                Accent = Color.FromArgb(255, 137, 220, 235), AccentHover = Color.FromArgb(255, 168, 232, 243), AccentPressed = Color.FromArgb(255, 96, 192, 208) },
        new() { Id = "cat_lavender", BaseTheme = "Catppuccin", DisplayName = "Lavender",
                Accent = Color.FromArgb(255, 180, 190, 254), AccentHover = Color.FromArgb(255, 206, 212, 255), AccentPressed = Color.FromArgb(255, 144, 160, 224) },

        // ── DRACULA PALETTE (9 Colors) ──
        new() { Id = "dra_purple", BaseTheme = "Dracula", DisplayName = "Purple",
                Accent = Color.FromArgb(255, 189, 147, 249), AccentHover = Color.FromArgb(255, 208, 176, 255), AccentPressed = Color.FromArgb(255, 160, 112, 224) },
        new() { Id = "dra_pink", BaseTheme = "Dracula", DisplayName = "Pink",
                Accent = Color.FromArgb(255, 255, 121, 198), AccentHover = Color.FromArgb(255, 255, 158, 213), AccentPressed = Color.FromArgb(255, 224, 80, 160) },
        new() { Id = "dra_cyan", BaseTheme = "Dracula", DisplayName = "Cyan",
                Accent = Color.FromArgb(255, 139, 233, 253), AccentHover = Color.FromArgb(255, 176, 240, 255), AccentPressed = Color.FromArgb(255, 96, 208, 224) },
        new() { Id = "dra_green", BaseTheme = "Dracula", DisplayName = "Green",
                Accent = Color.FromArgb(255, 80, 250, 123), AccentHover = Color.FromArgb(255, 117, 255, 149), AccentPressed = Color.FromArgb(255, 48, 216, 85) },
        new() { Id = "dra_orange", BaseTheme = "Dracula", DisplayName = "Orange",
                Accent = Color.FromArgb(255, 255, 184, 108), AccentHover = Color.FromArgb(255, 255, 203, 138), AccentPressed = Color.FromArgb(255, 224, 152, 80) },
        new() { Id = "dra_yellow", BaseTheme = "Dracula", DisplayName = "Yellow",
                Accent = Color.FromArgb(255, 241, 250, 140), AccentHover = Color.FromArgb(255, 247, 255, 168), AccentPressed = Color.FromArgb(255, 208, 224, 96) },
        new() { Id = "dra_red", BaseTheme = "Dracula", DisplayName = "Red",
                Accent = Color.FromArgb(255, 255, 85, 85), AccentHover = Color.FromArgb(255, 255, 117, 117), AccentPressed = Color.FromArgb(255, 208, 48, 48) },
        new() { Id = "dra_slate", BaseTheme = "Dracula", DisplayName = "Slate",
                Accent = Color.FromArgb(255, 98, 114, 164), AccentHover = Color.FromArgb(255, 122, 138, 184), AccentPressed = Color.FromArgb(255, 72, 88, 128) },
        new() { Id = "dra_white", BaseTheme = "Dracula", DisplayName = "White",
                Accent = Color.FromArgb(255, 248, 248, 242), AccentHover = Color.FromArgb(255, 255, 255, 255), AccentPressed = Color.FromArgb(255, 208, 208, 192) },

        // ── TOKYO NIGHT PALETTE (9 Colors) ──
        new() { Id = "tokyo_blue", BaseTheme = "Tokyo Night", DisplayName = "Blue",
                Accent = Color.FromArgb(255, 122, 162, 247), AccentHover = Color.FromArgb(255, 155, 184, 255), AccentPressed = Color.FromArgb(255, 90, 130, 208) },
        new() { Id = "tokyo_purple", BaseTheme = "Tokyo Night", DisplayName = "Purple",
                Accent = Color.FromArgb(255, 187, 154, 247), AccentHover = Color.FromArgb(255, 208, 176, 255), AccentPressed = Color.FromArgb(255, 154, 112, 224) },
        new() { Id = "tokyo_cyan", BaseTheme = "Tokyo Night", DisplayName = "Cyan",
                Accent = Color.FromArgb(255, 125, 207, 255), AccentHover = Color.FromArgb(255, 160, 223, 255), AccentPressed = Color.FromArgb(255, 80, 176, 224) },
        new() { Id = "tokyo_green", BaseTheme = "Tokyo Night", DisplayName = "Green",
                Accent = Color.FromArgb(255, 158, 206, 106), AccentHover = Color.FromArgb(255, 181, 224, 128), AccentPressed = Color.FromArgb(255, 122, 168, 80) },
        new() { Id = "tokyo_red", BaseTheme = "Tokyo Night", DisplayName = "Red",
                Accent = Color.FromArgb(255, 247, 118, 142), AccentHover = Color.FromArgb(255, 255, 149, 168), AccentPressed = Color.FromArgb(255, 208, 80, 96) },
        new() { Id = "tokyo_orange", BaseTheme = "Tokyo Night", DisplayName = "Orange",
                Accent = Color.FromArgb(255, 255, 158, 100), AccentHover = Color.FromArgb(255, 255, 184, 133), AccentPressed = Color.FromArgb(255, 224, 128, 64) },
        new() { Id = "tokyo_yellow", BaseTheme = "Tokyo Night", DisplayName = "Yellow",
                Accent = Color.FromArgb(255, 224, 175, 104), AccentHover = Color.FromArgb(255, 240, 200, 128), AccentPressed = Color.FromArgb(255, 200, 144, 64) },
        new() { Id = "tokyo_magenta", BaseTheme = "Tokyo Night", DisplayName = "Magenta",
                Accent = Color.FromArgb(255, 255, 0, 124), AccentHover = Color.FromArgb(255, 255, 77, 160), AccentPressed = Color.FromArgb(255, 208, 0, 96) },
        new() { Id = "tokyo_teal", BaseTheme = "Tokyo Night", DisplayName = "Teal",
                Accent = Color.FromArgb(255, 115, 218, 202), AccentHover = Color.FromArgb(255, 144, 240, 224), AccentPressed = Color.FromArgb(255, 80, 184, 168) },

        // ── ROSÉ PINE PALETTE (9 Colors) ──
        new() { Id = "rose_love", BaseTheme = "Rosé Pine", DisplayName = "Love",
                Accent = Color.FromArgb(255, 235, 111, 146), AccentHover = Color.FromArgb(255, 240, 144, 168), AccentPressed = Color.FromArgb(255, 208, 80, 112) },
        new() { Id = "rose_gold", BaseTheme = "Rosé Pine", DisplayName = "Gold",
                Accent = Color.FromArgb(255, 246, 193, 119), AccentHover = Color.FromArgb(255, 255, 213, 144), AccentPressed = Color.FromArgb(255, 224, 168, 80) },
        new() { Id = "rose_rose", BaseTheme = "Rosé Pine", DisplayName = "Rose",
                Accent = Color.FromArgb(255, 235, 188, 186), AccentHover = Color.FromArgb(255, 248, 213, 211), AccentPressed = Color.FromArgb(255, 208, 160, 160) },
        new() { Id = "rose_pine", BaseTheme = "Rosé Pine", DisplayName = "Pine",
                Accent = Color.FromArgb(255, 49, 116, 143), AccentHover = Color.FromArgb(255, 74, 144, 176), AccentPressed = Color.FromArgb(255, 32, 88, 112) },
        new() { Id = "rose_foam", BaseTheme = "Rosé Pine", DisplayName = "Foam",
                Accent = Color.FromArgb(255, 156, 207, 216), AccentHover = Color.FromArgb(255, 184, 229, 240), AccentPressed = Color.FromArgb(255, 128, 176, 184) },
        new() { Id = "rose_iris", BaseTheme = "Rosé Pine", DisplayName = "Iris",
                Accent = Color.FromArgb(255, 196, 167, 231), AccentHover = Color.FromArgb(255, 216, 192, 255), AccentPressed = Color.FromArgb(255, 160, 128, 200) },
        new() { Id = "rose_subtle", BaseTheme = "Rosé Pine", DisplayName = "Subtle",
                Accent = Color.FromArgb(255, 144, 140, 170), AccentHover = Color.FromArgb(255, 176, 168, 200), AccentPressed = Color.FromArgb(255, 112, 106, 136) },
        new() { Id = "rose_muted", BaseTheme = "Rosé Pine", DisplayName = "Muted",
                Accent = Color.FromArgb(255, 110, 106, 134), AccentHover = Color.FromArgb(255, 136, 132, 160), AccentPressed = Color.FromArgb(255, 80, 78, 104) },
        new() { Id = "rose_overlay", BaseTheme = "Rosé Pine", DisplayName = "Overlay",
                Accent = Color.FromArgb(255, 38, 35, 58), AccentHover = Color.FromArgb(255, 64, 60, 88), AccentPressed = Color.FromArgb(255, 16, 14, 24) },
    };

    public static AccentScheme Get(string? id)
    {
        if (string.IsNullOrEmpty(id)) return Schemes[0];
        foreach (var s in Schemes) if (s.Id == id) return s;
        return Schemes[0];
    }

    public static void ApplyFont(Application app, bool useThemeFont)
    {
        // Load Roboto directly from the Assets/font folder, with Segoe UI as fallback
        FontFamily targetFont = new FontFamily("ms-appx:///Assets/font/Roboto-Regular.ttf#Roboto, Segoe UI Variable Text");

        if (useThemeFont)
        {
            // Look up the font from the currently active theme dictionary
            if (app.Resources.TryGetValue("ThemeFontFamily", out var fontObj) && fontObj is FontFamily themeFont)
            {
                targetFont = themeFont;
            }
        }

        // Apply globally to all controls
        app.Resources["ContentControlThemeFontFamily"] = targetFont;

        // Force the window to redraw so the font visually updates
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            var currentTheme = root.RequestedTheme;
            root.RequestedTheme = ElementTheme.Light;
            root.RequestedTheme = ElementTheme.Dark;
            root.RequestedTheme = currentTheme;
        }
    }

    public static void Apply(Application app, AccentScheme scheme)
    {
        var dict = new ResourceDictionary();
        dict.ThemeDictionaries["Dark"] = BuildTheme(scheme);
        dict.ThemeDictionaries["Light"] = BuildTheme(scheme);
        dict.ThemeDictionaries["HighContrast"] = BuildTheme(scheme);

        dict["__StrataAccentOverride"] = true;

        var merged = app.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i] is ResourceDictionary rd && rd.ContainsKey("__StrataAccentOverride"))
            {
                merged.RemoveAt(i);
            }
        }
        merged.Add(dict);
    }

    public static void ApplyBaseTheme(Application app, string baseTheme)
    {
        var merged = app.Resources.MergedDictionaries;

        for (int i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i].Source != null && merged[i].Source.OriginalString.Contains("Styles/Themes/"))
            {
                merged.RemoveAt(i);
            }
        }

        string uri = baseTheme switch
        {
            "Catppuccin" => "ms-appx:///Styles/Themes/Catppuccin.xaml",
            "Dracula" => "ms-appx:///Styles/Themes/Dracula.xaml",
            "Tokyo Night" => "ms-appx:///Styles/Themes/TokyoNight.xaml",
            "Rosé Pine" => "ms-appx:///Styles/Themes/RosePine.xaml",
            _ => "ms-appx:///Styles/Themes/Aura.xaml"
        };

        try
        {
            merged.Add(new ResourceDictionary { Source = new Uri(uri) });
        }
        catch
        {
            merged.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Styles/Themes/Aura.xaml") });
        }
    }

    private static ResourceDictionary BuildTheme(AccentScheme s)
    {
        var d = new ResourceDictionary
        {
            ["SystemAccentColor"] = s.Accent,
            ["SystemAccentColorLight1"] = s.AccentHover,
            ["SystemAccentColorLight2"] = s.AccentHover,
            ["SystemAccentColorLight3"] = s.AccentHover,
            ["SystemAccentColorDark1"] = s.AccentPressed,
            ["SystemAccentColorDark2"] = s.AccentPressed,
            ["SystemAccentColorDark3"] = s.AccentPressed,
            ["AccentFillColorDefaultBrush"] = new SolidColorBrush(s.Accent),
            ["AccentFillColorSecondaryBrush"] = new SolidColorBrush(s.AccentHover),
            ["AccentFillColorTertiaryBrush"] = new SolidColorBrush(s.AccentPressed),
            ["AccentTextFillColorPrimaryBrush"] = new SolidColorBrush(s.AccentHover),
            ["AccentTextFillColorSecondaryBrush"] = new SolidColorBrush(s.AccentHover),
            ["AccentTextFillColorTertiaryBrush"] = new SolidColorBrush(s.AccentPressed),
            ["TextOnAccentFillColorPrimaryBrush"] = new SolidColorBrush(
                s.Accent.R > 180 && s.Accent.G > 180 && s.Accent.B > 180
                    ? Color.FromArgb(255, 30, 30, 30)
                    : Color.FromArgb(255, 255, 255, 255)),
            ["TransparentBrush"] = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),

            // Force specific controls to respect the accent
            ["RadioButtonForegroundChecked"] = new SolidColorBrush(s.Accent),
            ["RadioButtonOuterEllipseCheckedFill"] = new SolidColorBrush(s.Accent),
            ["RadioButtonOuterEllipseCheckedStroke"] = new SolidColorBrush(s.Accent),
            ["ToggleSwitchFillOn"] = new SolidColorBrush(s.Accent),
            ["ToggleSwitchFillOnPointerOver"] = new SolidColorBrush(s.AccentHover),
            ["ToggleSwitchFillOnPressed"] = new SolidColorBrush(s.AccentPressed),
            ["SliderTrackValueFill"] = new SolidColorBrush(s.Accent),
            ["SliderTrackValueFillPointerOver"] = new SolidColorBrush(s.AccentHover),
            ["SliderThumbBackground"] = new SolidColorBrush(s.Accent),
            ["SliderThumbBackgroundPointerOver"] = new SolidColorBrush(s.AccentHover)
        };
        return d;
    }
}