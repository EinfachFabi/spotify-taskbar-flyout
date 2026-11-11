using Microsoft.Win32;

namespace SpotifyTaskbarFlyout;

/// <summary>Colors matching the Windows light or dark system theme.</summary>
internal sealed record Theme(
    bool IsLight,
    Color Background,
    Color Footer,
    Color Border,
    Color Foreground,
    Color Secondary,
    Color Tertiary,
    Color Disabled,
    Color Hover,
    Color Pressed,
    Color ControlBg,
    Color ControlHover,
    Color ControlPressed,
    Color Track,
    Color Accent,
    Color AccentHover,
    Color AccentPressed)
{
    public static Theme FromSystem()
    {
        bool light = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch
        {
            // Default: dark
        }

        return light
            ? new Theme(
                IsLight: true,
                Background: Color.FromArgb(242, 242, 242),
                Footer: Color.FromArgb(232, 232, 232),
                Border: Color.FromArgb(212, 212, 212),
                Foreground: Color.FromArgb(26, 26, 26),
                Secondary: Color.FromArgb(95, 95, 95),
                Tertiary: Color.FromArgb(130, 130, 130),
                Disabled: Color.FromArgb(175, 175, 175),
                Hover: Color.FromArgb(228, 228, 228),
                Pressed: Color.FromArgb(214, 214, 214),
                ControlBg: Color.FromArgb(252, 252, 252),
                ControlHover: Color.FromArgb(245, 245, 245),
                ControlPressed: Color.FromArgb(235, 235, 235),
                Track: Color.FromArgb(204, 204, 204),
                Accent: Color.FromArgb(0x1D, 0xB9, 0x54),
                AccentHover: Color.FromArgb(0x1E, 0xD7, 0x60),
                AccentPressed: Color.FromArgb(0x16, 0x9C, 0x46))
            : new Theme(
                IsLight: false,
                Background: Color.FromArgb(36, 36, 36),
                Footer: Color.FromArgb(28, 28, 28),
                Border: Color.FromArgb(58, 58, 58),
                Foreground: Color.FromArgb(245, 245, 245),
                Secondary: Color.FromArgb(176, 176, 176),
                Tertiary: Color.FromArgb(128, 128, 128),
                Disabled: Color.FromArgb(92, 92, 92),
                Hover: Color.FromArgb(52, 52, 52),
                Pressed: Color.FromArgb(66, 66, 66),
                ControlBg: Color.FromArgb(50, 50, 50),
                ControlHover: Color.FromArgb(62, 62, 62),
                ControlPressed: Color.FromArgb(74, 74, 74),
                Track: Color.FromArgb(76, 76, 76),
                Accent: Color.FromArgb(0x1D, 0xB9, 0x54),
                AccentHover: Color.FromArgb(0x1E, 0xD7, 0x60),
                AccentPressed: Color.FromArgb(0x16, 0x9C, 0x46));
    }
}
