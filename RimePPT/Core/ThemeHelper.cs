using System;
using Microsoft.UI.Xaml;

namespace RimePPT.Core
{
    /// <summary>
    /// 主题判定统一入口：设置强制浅/深色优先；auto 时读系统注册表
    /// （AppsUseLightTheme），因为逐窗口的 ActualTheme 实测可能不一致
    /// （同批创建的浮窗读到不同的深浅），注册表值全局确定。
    /// </summary>
    public static class ThemeHelper
    {
        private const string PersonalizeKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string AppsUseLightTheme = "AppsUseLightTheme";

        public static bool IsDarkTheme()
        {
            switch (AppSettings.Instance.Theme)
            {
                case "dark":
                    return true;
                case "light":
                    return false;
                default:
                    return IsSystemAppModeDark();
            }
        }

        private static bool IsSystemAppModeDark()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                if (key?.GetValue(AppsUseLightTheme) is int light && light == 0)
                {
                    return true;
                }
                if (key?.GetValue(AppsUseLightTheme) is long light2 && light2 == 0)
                {
                    return true;
                }
                return false;
            }
            catch
            {
                // 读不到注册表时退回应用级主题（应用启动时的系统模式）
                return Application.Current.RequestedTheme == ApplicationTheme.Dark;
            }
        }
    }
}
