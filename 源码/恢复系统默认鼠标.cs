using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class ResetSystemCursor
{
    static readonly string[] Roles = { "Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen", "No", "SizeNS", "SizeWE", "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand", "Pin", "Person" };
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Refresh(uint action, uint param, IntPtr value, uint flags);
    static Dictionary<string, string> ReadDefaults()
    {
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Resources\Themes\aero.theme");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool active = false;
        foreach (string raw in File.ReadAllLines(file))
        {
            string line = raw.Trim();
            if (line.StartsWith("[")) { active = line.Equals(@"[Control Panel\Cursors]", StringComparison.OrdinalIgnoreCase); continue; }
            int equals = line.IndexOf('=');
            if (active && equals > 0) result[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
        }
        if (!result.ContainsKey("Arrow") || !result.ContainsKey("IBeam")) throw new IOException("未找到完整的 Windows 默认鼠标方案。");
        foreach (string role in Roles)
        {
            if (!result.ContainsKey(role)) result[role] = "";
            string path = Environment.ExpandEnvironmentVariables(result[role]);
            if (path.Length > 0 && !File.Exists(path)) throw new IOException("系统默认鼠标文件缺失：" + path);
        }
        return result;
    }
    static void Reload()
    {
        if (!Refresh(0x57, 0, IntPtr.Zero, 0)) throw new IOException("Windows 刷新鼠标失败，错误码 " + Marshal.GetLastWin32Error());
    }
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        try
        {
            var defaults = ReadDefaults();
            if (args.Length == 2 && args[0] == "--check") { File.WriteAllText(args[1], "Windows default theme and all referenced files validated. No settings modified."); return; }
            var names = new List<string>(Roles); names.Add(""); names.Add("Scheme Source");
            using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Cursors"))
            {
                var values = new Dictionary<string, object>();
                var kinds = new Dictionary<string, RegistryValueKind>();
                foreach (string name in names)
                {
                    values[name] = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (values[name] != null) kinds[name] = key.GetValueKind(name);
                }
                try
                {
                    foreach (string role in Roles) key.SetValue(role, defaults[role], RegistryValueKind.ExpandString);
                    key.SetValue("", "Windows Default", RegistryValueKind.String);
                    key.SetValue("Scheme Source", 2, RegistryValueKind.DWord);
                    Reload();
                }
                catch (Exception error)
                {
                    foreach (string name in names)
                    {
                        if (values[name] == null) key.DeleteValue(name, false);
                        else key.SetValue(name, values[name], kinds[name]);
                    }
                    try { Reload(); }
                    catch (Exception rollback) { throw new IOException(error.Message + "\n恢复刷新也失败：" + rollback.Message); }
                    throw;
                }
            }
            MessageBox.Show("已恢复 Windows 系统默认鼠标，可以开始测试 skill 了。", "恢复系统默认鼠标", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            if (args.Length == 2 && args[0] == "--check") File.WriteAllText(args[1], error.ToString());
            else MessageBox.Show(error.Message, "恢复未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
