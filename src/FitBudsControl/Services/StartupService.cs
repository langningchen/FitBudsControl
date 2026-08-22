using Microsoft.Win32;

namespace FitBudsControl.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FitBudsControl";

    public static bool TryApply(bool enabled, out string error)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "无法访问当前用户的开机启动设置";
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                error = string.Empty;
                return true;
            }

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                error = "无法确定程序位置";
                return false;
            }

            key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            error = "无法修改开机启动设置";
            return false;
        }
    }
}
