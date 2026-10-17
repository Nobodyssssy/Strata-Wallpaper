using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.UI;

namespace Blueprint.ControlPanel.Services;

public sealed class AccentScheme
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public Color Accent { get; init; }
    public Color AccentHover { get; init; }
    public Color AccentPressed { get; init; }
}

public static class ThemeService
{
    public static readonly IReadOnlyList<AccentScheme> Schemes = new List<AccentScheme>
    {
        new() { Id = "default",    DisplayName = "Blueprint Blue",
                Accent        = Color.FromArgb(255,  74, 144, 226),
                AccentHover   = Color.FromArgb(255,  96, 165, 250),
                AccentPressed = Color.FromArgb(255,  59, 130, 246) },

        new() { Id = "catppuccin", DisplayName = "Catppuccin",
                Accent        = Color.FromArgb(255, 203, 166, 247),
                AccentHover   = Color.FromArgb(255, 220, 190, 255),
                AccentPressed = Color.FromArgb(255, 180, 140, 230) },

        new() { Id = "tokyonight", DisplayName = "Tokyo Night",
                Accent        = Color.FromArgb(255, 122, 162, 247),
                AccentHover   = Color.FromArgb(255, 150, 185, 255),
                AccentPressed = Color.FromArgb(255, 100, 140, 220) },

        new() { Id = "dracula",    DisplayName = "Dracula",
                Accent        = Color.FromArgb(255, 189, 147, 249),
                AccentHover   = Color.FromArgb(255, 210, 175, 255),
                AccentPressed = Color.FromArgb(255, 165, 120, 230) },

        new() { Id = "gruvbox",    DisplayName = "Gruvbox",
                Accent        = Color.FromArgb(255, 250, 189,  47),
                AccentHover   = Color.FromArgb(255, 255, 210,  90),
                AccentPressed = Color.FromArgb(255, 215, 155,  30) },

        new() { Id = "nord",       DisplayName = "Nord",
                Accent        = Color.FromArgb(255, 136, 192, 208),
                AccentHover   = Color.FromArgb(255, 165, 215, 230),
                AccentPressed = Color.FromArgb(255, 110, 165, 185) },

        new() { Id = "rosepine",   DisplayName = "Rosé Pine",
                Accent        = Color.FromArgb(255, 235, 188, 186),
                AccentHover   = Color.FromArgb(255, 250, 210, 210),
                AccentPressed = Color.FromArgb(255, 210, 160, 160) },

        new() { Id = "everforest", DisplayName = "Everforest",
                Accent        = Color.FromArgb(255, 167, 192, 128),
                AccentHover   = Color.FromArgb(255, 190, 215, 155),
                AccentPressed = Color.FromArgb(255, 140, 170, 105) },
    };

    public static AccentScheme Get(string? id)
    {
        if (string.IsNullOrEmpty(id)) return Schemes[0];
        foreach (var s in Schemes) if (s.Id == id) return s;
        return Schemes[0];
    }

    public static void ApplyFont(Application app, bool useThemeFont)
    {
        // Load Roboto directly from the Assets folder, with Segoe UI as fallback
        FontFamily targetFont = new FontFamily("ms-appx:///Assets/Roboto-Regular.ttf#Roboto, Segoe UI Variable Text");

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