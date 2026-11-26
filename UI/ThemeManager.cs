using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Pemail.UI
{
    public class AppSettings
    {
        public bool DarkTheme { get; set; }
    }

    public static class ThemeManager
    {
        public static bool IsDark { get; private set; }

        private static string SettingsPath
        {
            get
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PemailData");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.json");
            }
        }

        public static void LoadAndApply()
        {
            bool dark = false;
            try
            {
                if (File.Exists(SettingsPath))
                {
                    JavaScriptSerializer json = new JavaScriptSerializer();
                    AppSettings s = json.Deserialize<AppSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8));
                    if (s != null) dark = s.DarkTheme;
                }
            }
            catch { }
            Apply(dark, false);
        }

        public static void Apply(bool dark, bool save = true)
        {
            IsDark = dark;
            ResourceDictionary res = Application.Current.Resources;

            if (dark)
            {
                // Мягкая, не слишком контрастная тёмная тема (Soft Slate Dark)
                SetBrush(res, "BgWindow", "#1E222A");
                SetBrush(res, "BgPanel", "#252A34");
                SetBrush(res, "BgSubtle", "#2B313D");
                SetBrush(res, "BgInput", "#2B313D");
                SetBrush(res, "BgHover", "#323947");
                SetBrush(res, "BgSelected", "#2A3B5C");
                SetBrush(res, "BorderSubtle", "#384050");
                SetBrush(res, "BorderInput", "#434C5E");
                SetBrush(res, "TextPrimary", "#D8DEE9");
                SetBrush(res, "TextSecondary", "#B0B8C8");
                SetBrush(res, "TextMuted", "#848D9F");
                SetBrush(res, "AccentBrush", "#4C8BF5");
                SetBrush(res, "BtnBg", "#2D3340");
                SetBrush(res, "BtnHover", "#373F4F");
                SetBrush(res, "BtnPressed", "#40495C");
                SetBrush(res, "BtnBorder", "#434C5E");
                SetBrush(res, "BtnFg", "#D8DEE9");
                SetBrush(res, "ArchiveBtnBg", "#1E332B");
                SetBrush(res, "ArchiveBtnHover", "#254035");
                SetBrush(res, "ArchiveBtnBorder", "#2E5445");
                SetBrush(res, "ArchiveBtnFg", "#6EE7B7");
                SetBrush(res, "DangerBtnBg", "#3A2226");
                SetBrush(res, "DangerBtnHover", "#48292E");
                SetBrush(res, "DangerBtnBorder", "#63353C");
                SetBrush(res, "DangerBtnFg", "#FCA5A5");
                SetBrush(res, "ToggleCheckedBg", "#293B5E");
                SetBrush(res, "ToggleCheckedBorder", "#4C8BF5");
                SetBrush(res, "ToggleCheckedFg", "#93C5FD");
                SetBrush(res, "IconStroke", "#B0B8C8");
                SetBrushObj(res, SystemColors.HighlightBrushKey, "#2E4063");
                SetBrushObj(res, SystemColors.HighlightTextBrushKey, "#E5E9F0");
                SetBrushObj(res, SystemColors.InactiveSelectionHighlightBrushKey, "#2B3548");
                SetBrushObj(res, SystemColors.InactiveSelectionHighlightTextBrushKey, "#D8DEE9");
            }
            else
            {
                SetBrush(res, "BgWindow", "#F4F6F9");
                SetBrush(res, "BgPanel", "#FFFFFF");
                SetBrush(res, "BgSubtle", "#F9FAFB");
                SetBrush(res, "BgInput", "#FFFFFF");
                SetBrush(res, "BgHover", "#F3F4F6");
                SetBrush(res, "BgSelected", "#DBEAFE");
                SetBrush(res, "BorderSubtle", "#E2E6EC");
                SetBrush(res, "BorderInput", "#D1D5DB");
                SetBrush(res, "TextPrimary", "#111827");
                SetBrush(res, "TextSecondary", "#374151");
                SetBrush(res, "TextMuted", "#6B7280");
                SetBrush(res, "AccentBrush", "#2563EB");
                SetBrush(res, "BtnBg", "#FFFFFF");
                SetBrush(res, "BtnHover", "#F3F4F6");
                SetBrush(res, "BtnPressed", "#E5E7EB");
                SetBrush(res, "BtnBorder", "#D1D5DB");
                SetBrush(res, "BtnFg", "#1F2937");
                SetBrush(res, "ArchiveBtnBg", "#ECFDF5");
                SetBrush(res, "ArchiveBtnHover", "#D1FAE5");
                SetBrush(res, "ArchiveBtnBorder", "#A7F3D0");
                SetBrush(res, "ArchiveBtnFg", "#047857");
                SetBrush(res, "DangerBtnBg", "#FEF2F2");
                SetBrush(res, "DangerBtnHover", "#FEE2E2");
                SetBrush(res, "DangerBtnBorder", "#FECACA");
                SetBrush(res, "DangerBtnFg", "#B91C1C");
                SetBrush(res, "ToggleCheckedBg", "#DBEAFE");
                SetBrush(res, "ToggleCheckedBorder", "#60A5FA");
                SetBrush(res, "ToggleCheckedFg", "#1D4ED8");
                SetBrush(res, "IconStroke", "#4B5563");
                SetBrushObj(res, SystemColors.HighlightBrushKey, "#DBEAFE");
                SetBrushObj(res, SystemColors.HighlightTextBrushKey, "#111827");
                SetBrushObj(res, SystemColors.InactiveSelectionHighlightBrushKey, "#E8F0FE");
                SetBrushObj(res, SystemColors.InactiveSelectionHighlightTextBrushKey, "#111827");
            }

            if (save)
            {
                try
                {
                    JavaScriptSerializer json = new JavaScriptSerializer();
                    File.WriteAllText(SettingsPath, json.Serialize(new AppSettings { DarkTheme = dark }), Encoding.UTF8);
                }
                catch { }
            }
        }

        private static void SetBrush(ResourceDictionary res, string key, string hex)
        {
            SetBrushObj(res, key, hex);
        }

        private static void SetBrushObj(ResourceDictionary res, object key, string hex)
        {
            Color c = (Color)ColorConverter.ConvertFromString(hex);
            SolidColorBrush brush = new SolidColorBrush(c);
            brush.Freeze();
            res[key] = brush;
        }
    }
}
