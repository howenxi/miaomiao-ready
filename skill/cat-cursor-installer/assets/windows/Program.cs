using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && (args[0] == "--self-test" || args[0] == "--integration-test"))
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "failed.txt"), e.ExceptionObject.ToString()); };
        if (args.Length == 2 && args[0] == "--integration-test")
        {
            Setup.Root = Path.GetFullPath(args[1]);
            Setup.Prepare(); Setup.CopyCursors(); Setup.SaveBackup();
            try { Setup.Apply(); foreach (int size in new int[] {25, 35, 45}) { Setup.ApplySize(size); Setup.VerifyNativeSizes(); } }
            finally { Setup.Restore(); }
            Setup.VerifyRestored();
            File.WriteAllText(Path.Combine(Setup.Root, "integration-passed.txt"), "Applied all eight cursors, refreshed Windows, restored original values, refreshed Windows again, verified registry values and types match backup.");
            return;
        }
        if (args.Length == 2 && args[0] == "--self-test")
        {
            Setup.SelfTesting = true;
            Setup.TestCatSelection(args[1]);
            Application.EnableVisualStyles();
            Setup.Root = Path.GetFullPath(args[1]);
            Setup.Prepare(); Setup.CopyCursors();
            AntInput.VerifyBehavior();
            using (var form = new InstallerWindow(false))
            {
                form.PreviewLoading(); form.Show(); Application.DoEvents();
                using (var image = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(Setup.Root, "loading.png")); }
                form.Preview(); Application.DoEvents();
                form.VerifySizePreviews();
                using (var image = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(Setup.Root, "preview.png")); }
            }
            File.WriteAllText(Path.Combine(Setup.Root, "passed.txt"), "Eight bundled cursors validated by Windows. Completion UI rendered. No mouse settings changed.");
            return;
        }
        bool created;
        using (var mutex = new Mutex(true, "Local\\MiaoMiaoReady-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value, out created))
        {
            if (!created) { MessageBox.Show("喵喵正在工作，请查看已打开的窗口。", "喵喵就位"); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool restoring = (args.Length > 0 && args[0] == "--restore") || Path.GetFileName(Application.ExecutablePath).StartsWith("恢复");
            if (!restoring && !Setup.ChooseCats()) return;
            Application.Run(new InstallerWindow(restoring));
        }
    }
}

internal static class Setup
{
    internal static readonly string[] Names = { "正常选择", "文字输入", "链接点击", "不可用", "忙碌中", "移动画布", "左右调整", "上下调整" };
    internal static readonly string[] Roles = { "Arrow", "IBeam", "Hand", "No", "Wait", "SizeAll", "SizeWE", "SizeNS" };
    internal static string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiaoMiaoReady");
    internal static string Backup { get { return Path.Combine(Root, "original.xml"); } }
    const string CursorKey = @"Control Panel\Cursors";
    const string AccessibilityKey = @"Software\Microsoft\Accessibility";
    internal static bool Changed;
    static readonly int[] choices = new int[8];
    static string ThemeFolder { get { return "cursors-v1" + (variantMode ? "-" + string.Join("_", Array.ConvertAll(choices, n => n.ToString())) : ""); } }
    static bool variantMode;

    internal static void TestCatSelection(string output)
    {
        using (var catalog = Resource("cats"))
        {
            if (catalog == null) return;
            Application.EnableVisualStyles();
            using (var dialog = new CatPicker(XDocument.Load(catalog)))
            {
                Directory.CreateDirectory(output);
                dialog.Show(); Application.DoEvents();
                using (var image = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                    image.Save(Path.Combine(output, "cat-picker.png"));
                }
                dialog.VerifyAssignments();
                Array.Copy(dialog.ResourceChoices(), choices, choices.Length);
                variantMode = true;
            }
        }
    }

    internal static bool ChooseCats()
    {
        using (var catalog = Resource("cats"))
        {
            if (catalog == null) return true;
            var cats = XDocument.Load(catalog);
            using (var dialog = new CatPicker(cats))
            {
                if (dialog.ShowDialog() != DialogResult.OK) return false;
                Array.Copy(dialog.ResourceChoices(), choices, choices.Length);
                variantMode = true;
                return true;
            }
        }
    }
    internal static bool Enabled(int index) { return choices[index] >= 0; }
    static Stream CursorResource(string kind, int index)
    {
        return Resource(choices[index] == 0 ? kind + index : kind + index + "-" + choices[index]);
    }
    static System.Collections.Generic.IEnumerable<XElement> ThemeEntries()
    {
        using (var stream = Resource("theme"))
        foreach (var entry in XDocument.Load(stream).Root.Elements("cursor"))
        {
            int index = (int)entry.Attribute("index");
            if (!Enabled(index)) continue;
            if (choices[index] == 0) { yield return entry; continue; }
            foreach (var variant in entry.Elements("variant"))
                if ((int)variant.Attribute("number") == choices[index]) { yield return variant; break; }
        }
    }
    internal static bool SelfTesting;
    internal static string SizedFolder;
    internal static int PreviewPixels = 35;
    internal static string CursorPath(int index)
    {
        return Path.Combine(SizedFolder ?? Path.Combine(Root, ThemeFolder), Names[index] + ".cur");
    }
    internal static void PrepareSize(int pixels)
    {
        if (pixels != 25 && pixels != 35 && pixels != 45) throw new ArgumentOutOfRangeException("pixels");
        string folder = Path.Combine(Root, ThemeFolder, "size-native-v3-" + pixels);
        if (!Directory.Exists(folder))
        {
            string staging = folder + "." + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var item in ThemeEntries())
                {
                    int index = (int)item.Attribute("index");
                    string png = Path.Combine(staging, index + ".png");
                    using (var source = CursorResource("picture", index))
                    using (var target = File.Create(png)) source.CopyTo(target);
                    string path = Path.Combine(staging, Names[index] + ".cur");
                    CursorEncoder.ConvertPixels(png, path, (int)item.Attribute("x"), (int)item.Attribute("y"), pixels);
                    IntPtr handle = LoadCursorFromFile(path);
                    if (handle == IntPtr.Zero) throw new IOException("预览指针生成失败");
                    DestroyCursor(handle); File.Delete(png);
                }
                Directory.Move(staging, folder);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        SizedFolder = folder; PreviewPixels = pixels;
    }
    internal static void ApplySize(int pixels)
    {
        var previous = new XElement("cursorBackup");
        previous.SetAttributeValue("runtimeCursorBaseSize", RuntimeCursorBaseSize());
        foreach (string role in Roles) previous.Add(Capture(CursorKey, role));
        previous.Add(Capture(CursorKey, "")); previous.Add(Capture(CursorKey, "Scheme Source"));
        previous.Add(Capture(CursorKey + @"\Schemes", "喵喵就位"));
        previous.Add(Capture(CursorKey, "CursorBaseSize"));
        previous.Add(Capture(AccessibilityKey, "CursorSize"));
        try
        {
            PrepareSize(pixels); Apply();
            string setting = Path.Combine(Root, ThemeFolder, "size.txt");
            string temp = setting + ".tmp";
            File.WriteAllText(temp, pixels.ToString());
            if (File.Exists(setting)) File.Replace(temp, setting, null); else File.Move(temp, setting);
        }
        catch { RestoreValues(new XDocument(previous)); throw; }
    }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
    // These Windows size actions update/query the live accessibility multiplier.
    // SPI_SETCURSORS only reloads artwork; writing the registry alone leaves the
    // old live multiplier in place. Verify the live value, not just registry.
    internal static int RuntimeCursorBaseSize()
    {
        IntPtr value = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(value, 0);
            return SystemParametersInfo(0x2028, 0, value, 0) ? Marshal.ReadInt32(value) : 0;
        }
        finally { Marshal.FreeHGlobal(value); }
    }
    static void SetRuntimeCursorBaseSize(int size, bool required)
    {
        if (size < 1 || size > 256) throw new IOException("鼠标基础大小不正确");
        // Persistence is managed by our explicit registry snapshot. Flags=0
        // avoids recreating values that were absent in the original settings.
        if (!SystemParametersInfo(0x2029, 0, new IntPtr(size), 0) || RuntimeCursorBaseSize() != size)
        {
            if (required) throw new IOException("Windows 未能同步实时鼠标大小，请先在系统鼠标设置中将大小调到最小后重试。");
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendNotifyMessageW")]
    static extern bool SendNotifyMessage(IntPtr window, uint message, IntPtr wParam, string lParam);
    static void NotifyCursorSettings()
    {
        SendNotifyMessage(new IntPtr(0xffff), 0x1a, IntPtr.Zero, CursorKey);
        SendNotifyMessage(new IntPtr(0xffff), 0x1a, IntPtr.Zero, AccessibilityKey);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadCursorFromFile(string path);
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapHeader { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int X, Y; public uint Used, Important; }
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader header, uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bits, byte[] data);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref CursorInfo info);
    // Windows 11 otherwise virtualizes in-memory cursors for the monitor DPI.
    // Keep the agreed 25/35/45 physical pixels, just like the unscaled preview.
    [DllImport("user32.dll")] static extern uint SetThreadCursorCreationScaling(uint dpi);
    internal static bool FixedPixelScalingAvailable;
    internal static IntPtr LoadPreviewCursor(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int offset = BitConverter.ToInt32(data, 18);
        int width = BitConverter.ToInt32(data, offset + 4), height = BitConverter.ToInt32(data, offset + 8) / 2;
        var header = new BitmapHeader { Size = 40, Width = width, Height = height, Planes = 1, Bits = 32 };
        IntPtr pixels;
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out pixels, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) throw new IOException("鼠标像素缓冲区创建失败");
        IntPtr mask = IntPtr.Zero;
        try
        {
            Marshal.Copy(data, offset + 40, pixels, width * height * 4);
            mask = CreateBitmap(width, height, 1, 1, new byte[((width + 15) / 16) * 2 * height]);
            if (mask == IntPtr.Zero) throw new IOException("鼠标透明蒙版创建失败");
            var info = new CursorInfo { Icon = false, X = BitConverter.ToUInt16(data, 10), Y = BitConverter.ToUInt16(data, 12), Color = color, Mask = mask };
            uint previous = 0;
            bool changed = false;
            try
            {
                try { previous = SetThreadCursorCreationScaling(1); changed = true; FixedPixelScalingAvailable = true; }
                catch (EntryPointNotFoundException) { } // Windows 10 uses the legacy creation path.
                IntPtr cursor = CreateIconIndirect(ref info);
                if (cursor == IntPtr.Zero) throw new IOException("鼠标创建失败");
                return cursor;
            }
            finally { if (changed) SetThreadCursorCreationScaling(previous); }
        }
        finally { DeleteObject(color); if (mask != IntPtr.Zero) DeleteObject(mask); }
    }
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetSystemCursor(IntPtr cursor, uint id);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, IntPtr id);
    internal static void VerifyNativeSizes()
    {
        if (RuntimeCursorBaseSize() != 32) throw new IOException("Windows 实时鼠标放大倍率尚未归一");
        using (var key = Registry.CurrentUser.OpenSubKey(CursorKey))
            if (Convert.ToInt32(key.GetValue("CursorBaseSize", 32)) != 32) throw new IOException("系统仍在额外放大鼠标");
        using (var key = Registry.CurrentUser.OpenSubKey(AccessibilityKey))
            if (key != null && Convert.ToInt32(key.GetValue("CursorSize", 1)) != 1) throw new IOException("系统仍在额外放大鼠标");
        uint[] ids = {32512, 32513, 32649, 32648, 32514, 32646, 32644, 32645};
        using (var stream = Resource("theme"))
        {
            foreach (var item in ThemeEntries())
            {
                int index = (int)item.Attribute("index");
                Size size = NativeCursorSize(LoadCursor(IntPtr.Zero, new IntPtr(ids[index])));
                if (size.Width != 64 || size.Height != 64) throw new IOException("系统重缩放了鼠标：" + Names[index]);
                VerifyCursorPixels(LoadCursor(IntPtr.Zero, new IntPtr(ids[index])), CursorPath(index));
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapReadInfo { public BitmapHeader Header; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public uint[] Colors; }
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] pixels, ref BitmapReadInfo header, uint usage);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] static extern IntPtr GetCursor();
    internal static void VerifyCursorPixels(IntPtr handle, string path)
    {
        byte[] expected = File.ReadAllBytes(path);
        int offset = BitConverter.ToInt32(expected, 18);
        int width = BitConverter.ToInt32(expected, offset + 4), height = BitConverter.ToInt32(expected, offset + 8) / 2;
        CursorInfo info;
        if (!GetIconInfo(handle, out info)) throw new IOException("无法读取实际鼠标像素");
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            Size actualSize = NativeCursorSize(handle);
            if (actualSize != new Size(width, height) || info.X != BitConverter.ToUInt16(expected, 10) || info.Y != BitConverter.ToUInt16(expected, 12))
                throw new IOException("实际鼠标尺寸或热点与预览不一致：" + Path.GetFileName(path) + "，实际" + actualSize + "，热点" + info.X + "," + info.Y);
            byte[] actual = new byte[width * height * 4];
            var header = new BitmapReadInfo { Header = new BitmapHeader { Size = 40, Width = width, Height = height, Planes = 1, Bits = 32 }, Colors = new uint[256] };
            if (GetDIBits(dc, info.Color, 0, (uint)height, actual, ref header, 0) != height) throw new IOException("无法读取实际鼠标位图");
            for (int i = 0; i < actual.Length; i++) if (actual[i] != expected[offset + 40 + i])
            {
                throw new IOException("试用鼠标像素与预览不一致：" + Path.GetFileName(path));
            }
        }
        finally { DeleteDC(dc); if (info.Color != IntPtr.Zero) DeleteObject(info.Color); if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask); }
    }
    internal static void VerifyTrialCursor(IntPtr handle, string path)
    {
        VerifyCursorPixels(handle, path);
        IntPtr previous = SetCursor(handle);
        try
        {
            // Check this UI thread. Global GetCursorInfo can belong to another
            // foreground app and must not be treated as our test cursor.
            VerifyCursorPixels(GetCursor(), path);
        }
        finally { SetCursor(previous); }
    }
    [StructLayout(LayoutKind.Sequential)]
    struct CursorInfo { public bool Icon; public uint X, Y; public IntPtr Mask, Color; }
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr cursor, out CursorInfo info);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
    internal static Size NativeCursorSize(IntPtr handle)
    {
        CursorInfo info;
        if (!GetIconInfo(handle, out info)) throw new IOException("无法读取鼠标尺寸");
        try
        {
            using (var bitmap = Image.FromHbitmap(info.Color)) return bitmap.Size;
        }
        finally { if (info.Color != IntPtr.Zero) DeleteObject(info.Color); if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask); }
    }
    static void ApplyNativeCursorSizes()
    {
        uint previous = 0; bool changed = false;
        try
        {
            try { previous = SetThreadCursorCreationScaling(1); changed = true; }
            catch (EntryPointNotFoundException) { }
            // SetSystemCursor makes another internal copy: keep the policy active
            // for that copy too, rather than only for CreateIconIndirect.
            ApplyNativeCursorSizesCore();
        }
        finally { if (changed) SetThreadCursorCreationScaling(previous); }
    }
    static void ApplyNativeCursorSizesCore()
    {
        uint[] ids = {32512, 32513, 32649, 32648, 32514, 32646, 32644, 32645};
        using (var key = Registry.CurrentUser.OpenSubKey(CursorKey))
        {
            for (int i = 0; i < Roles.Length; i++)
            {
                string path = Environment.ExpandEnvironmentVariables(Convert.ToString(key.GetValue(Roles[i], "")));
                if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                IntPtr handle = LoadPreviewCursor(path);
                if (handle == IntPtr.Zero) throw new IOException("无法载入鼠标：" + Names[i]);
                if (!SetSystemCursor(handle, ids[i])) { DestroyCursor(handle); throw new IOException("系统鼠标尺寸应用失败：" + Names[i]); }
            }
        }
    }
    [DllImport("user32.dll")] internal static extern bool DestroyCursor(IntPtr cursor);

    internal static Stream Resource(string name) { return Assembly.GetExecutingAssembly().GetManifestResourceStream(name); }
    internal static Image Picture(int index)
    {
        using (var stream = CursorResource("picture", index))
        using (var image = Image.FromStream(stream)) return new Bitmap(image);
    }
    internal static void Prepare()
    {
        Directory.CreateDirectory(Root);
        // Validate every bundled cursor before modifying the user's settings.
        for (int i = 0; i < Names.Length; i++)
        {
            if (!Enabled(i)) continue; string path = Path.Combine(Root, "check-" + i + ".cur");
            using (var source = CursorResource("cursor", i))
            using (var destination = File.Create(path)) source.CopyTo(destination);
            IntPtr handle = LoadCursorFromFile(path);
            if (handle == IntPtr.Zero) throw new IOException("无法读取指针文件：" + Names[i]);
            DestroyCursor(handle);
            File.Delete(path);
        }
    }
    static XElement Capture(string path, string name)
    {
        var element = new XElement("value", new XAttribute("path", path), new XAttribute("name", name));
        using (var key = Registry.CurrentUser.OpenSubKey(path))
        {
            object value = key == null ? null : key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            element.SetAttributeValue("exists", value != null);
            if (value != null)
            {
                var kind = key.GetValueKind(name);
                element.SetAttributeValue("kind", kind.ToString());
                if (kind == RegistryValueKind.Binary) element.Value = Convert.ToBase64String((byte[])value);
                else if (kind == RegistryValueKind.MultiString) foreach (string item in (string[])value) element.Add(new XElement("item", item));
                else element.Value = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return element;
    }
    internal static void SaveBackup()
    {
        if (File.Exists(Backup))
        {
            // Older backups did not contain size settings. Add only the missing
            // values before changing them; never replace the original scheme.
            var document = XDocument.Load(Backup);
            bool changed = false;
            foreach (var value in new XElement[] { Capture(CursorKey, "CursorBaseSize"), Capture(AccessibilityKey, "CursorSize") })
            {
                bool found = false;
                foreach (var existing in document.Root.Elements("value"))
                    if ((string)existing.Attribute("path") == (string)value.Attribute("path") && (string)existing.Attribute("name") == (string)value.Attribute("name")) found = true;
                if (!found) { document.Root.Add(value); changed = true; }
            }
            if (changed)
            {
                string upgrade = Backup + ".size-upgrade";
                document.Save(upgrade); File.Replace(upgrade, Backup, null);
            }
            if (document.Root.Attribute("runtimeCursorBaseSize") == null)
            {
                int size = 32;
                foreach (var value in document.Root.Elements("value"))
                    if ((string)value.Attribute("path") == CursorKey && (string)value.Attribute("name") == "CursorBaseSize" && (bool)value.Attribute("exists"))
                        size = int.Parse(value.Value, System.Globalization.CultureInfo.InvariantCulture);
                document.Root.SetAttributeValue("runtimeCursorBaseSize", size);
                string upgrade = Backup + ".size-upgrade";
                document.Save(upgrade); File.Replace(upgrade, Backup, null);
            }
            return;
        }
        var root = new XElement("cursorBackup");
        int runtimeSize = RuntimeCursorBaseSize();
        root.SetAttributeValue("runtimeCursorBaseSize", runtimeSize > 0 ? runtimeSize : 32);
        foreach (string role in Roles) root.Add(Capture(CursorKey, role));
        root.Add(Capture(CursorKey, ""));
        root.Add(Capture(CursorKey, "Scheme Source"));
        root.Add(Capture(CursorKey + @"\Schemes", "喵喵就位"));
        root.Add(Capture(CursorKey, "CursorBaseSize"));
        root.Add(Capture(AccessibilityKey, "CursorSize"));
        string temp = Backup + ".tmp";
        new XDocument(root).Save(temp);
        File.Move(temp, Backup);
    }
    internal static void CopyCursors()
    {
        // Stable content-specific directory keeps files used by an older install intact.
        string folder = Path.Combine(Root, ThemeFolder);
        Directory.CreateDirectory(folder);
        for (int i = 0; i < Names.Length; i++)
        {
            if (!Enabled(i)) continue; string target = Path.Combine(folder, Names[i] + ".cur");
            if (File.Exists(target)) continue;
            string temp = target + ".tmp";
            using (var source = CursorResource("cursor", i))
            using (var destination = File.Create(temp)) source.CopyTo(destination);
            File.Move(temp, target);
        }
        string previousFolder = SizedFolder;
        int previousSize = PreviewPixels;
        try { foreach (int size in new int[] {25, 35, 45}) PrepareSize(size); }
        finally { SizedFolder = previousFolder; PreviewPixels = previousSize; }
    }
    internal static void Apply()
    {
        if (SizedFolder == null)
        {
            int saved;
            string setting = Path.Combine(Root, ThemeFolder, "size.txt");
            if (File.Exists(setting) && int.TryParse(File.ReadAllText(setting), out saved)) PrepareSize(saved <= 25 ? 25 : saved <= 35 ? 35 : 45); else PrepareSize(35);
        }
        Changed = true;
        using (var key = Registry.CurrentUser.CreateSubKey(CursorKey))
        {
            for (int i = 0; i < Roles.Length; i++)
            {
                if (!Enabled(i)) continue; string path = CursorPath(i);
                IntPtr handle = LoadCursorFromFile(path);
                if (handle == IntPtr.Zero) throw new IOException("指针文件无法加载：" + Names[i]);
                DestroyCursor(handle);
                key.SetValue(Roles[i], path, RegistryValueKind.ExpandString);
            }
            string[] order = { "Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen", "No", "SizeNS", "SizeWE", "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand", "Pin", "Person" };
            var paths = new string[order.Length];
            for (int i = 0; i < order.Length; i++) paths[i] = Convert.ToString(key.GetValue(order[i], ""));
            using (var schemes = key.CreateSubKey("Schemes")) schemes.SetValue("喵喵就位", string.Join(",", paths), RegistryValueKind.String);
            key.SetValue("", "喵喵就位", RegistryValueKind.String);
            key.SetValue("Scheme Source", 1, RegistryValueKind.DWord);
            // The three presets already encode their final physical pixel size.
            // Accessibility magnification is independent of cursor-creation DPI
            // and otherwise enlarges the installed system cursor a second time.
            key.SetValue("CursorBaseSize", 32, RegistryValueKind.DWord);
        }
        using (var key = Registry.CurrentUser.CreateSubKey(AccessibilityKey)) key.SetValue("CursorSize", 1, RegistryValueKind.DWord);
        SetRuntimeCursorBaseSize(32, true);
        Reload(); ApplyNativeCursorSizes(); NotifyCursorSettings();
    }
    static void Reload()
    {
        // SPI_SETCURSORS reloads the saved scheme. It must use zero update flags.
        if (!SystemParametersInfo(0x57, 0, IntPtr.Zero, 0))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException("Windows 未能刷新鼠标方案（错误码 " + error + "）。");
        }
    }
    internal static void VerifyRestored()
    {
        var backup = XDocument.Load(Backup);
        int expectedSize = (int?)backup.Root.Attribute("runtimeCursorBaseSize") ?? 0;
        if (expectedSize > 0 && RuntimeCursorBaseSize() != expectedSize) throw new IOException("系统实时鼠标大小恢复验证失败");
        foreach (var expected in backup.Root.Elements("value"))
        {
            var actual = Capture((string)expected.Attribute("path"), (string)expected.Attribute("name"));
            if (!XNode.DeepEquals(expected, actual)) throw new IOException("恢复验证失败：" + (string)expected.Attribute("name"));
        }
    }
    internal static void Restore()
    {
        if (!File.Exists(Backup)) throw new IOException("还没有安装备份，无需恢复。");
        RestoreValues(XDocument.Load(Backup));
        Changed = false;
    }
    static void RestoreValues(XDocument document)
    {
        foreach (var element in document.Root.Elements("value"))
        {
            string path = (string)element.Attribute("path");
            if (path != CursorKey && path != CursorKey + @"\Schemes" && path != AccessibilityKey) throw new IOException("备份格式不正确。");
            string name = (string)element.Attribute("name");
            if (path == AccessibilityKey && name != "CursorSize") throw new IOException("备份大小设置不正确。");
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                if (!(bool)element.Attribute("exists")) { key.DeleteValue(name, false); continue; }
                var kind = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), (string)element.Attribute("kind"));
                object value = element.Value;
                if (kind == RegistryValueKind.DWord) value = int.Parse(element.Value, System.Globalization.CultureInfo.InvariantCulture);
                if (kind == RegistryValueKind.QWord) value = long.Parse(element.Value, System.Globalization.CultureInfo.InvariantCulture);
                if (kind == RegistryValueKind.Binary) value = Convert.FromBase64String(element.Value);
                if (kind == RegistryValueKind.MultiString) value = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(element.Elements("item"), x => x.Value));
                key.SetValue(name, value, kind);
            }
        }
        int originalSize = (int?)document.Root.Attribute("runtimeCursorBaseSize") ?? 0;
        if (originalSize > 0) SetRuntimeCursorBaseSize(originalSize, true);
        Reload(); ApplyNativeCursorSizes(); NotifyCursorSettings();
    }
}

internal sealed class CatPicker : Form
{
    internal readonly int[] Choices = new int[8];
    readonly Label count = new Label { AutoSize = true, ForeColor = Color.FromArgb(113, 90, 73) };
    readonly AssignmentCard[] assignments = new AssignmentCard[8];
    readonly string[] names;
    readonly int[] defaults;
    readonly FlowLayoutPanel cats = new FlowLayoutPanel();
    readonly Button apply = new AntButton { Text = "应用", Primary = true, DialogResult = DialogResult.OK };
    int selected;

    internal CatPicker(XDocument catalog)
    {
        var entries = new System.Collections.Generic.List<XElement>(catalog.Root.Elements("cat"));
        names = entries.ConvertAll(e => (string)e.Attribute("name")).ToArray();
        defaults = Array.ConvertAll(((string)catalog.Root.Attribute("defaults")).Split(','), int.Parse);
        Text = "喵喵就位 · 选择小猫";
        Font = new Font("Microsoft YaHei", 10);
        BackColor = Color.FromArgb(255, 248, 239);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(640, 526);

        var title = new Label { Text = "选一只猫，再分配指针", Font = new Font(Font.FontFamily, 19.5f, FontStyle.Bold), AutoSize = true, Left = 24, Top = 14 };
        Controls.Add(title);
        cats.SetBounds(21, 61, 598, 74);
        cats.AutoScroll = true; cats.WrapContents = false;
        Controls.Add(cats);
        for (int i = 0; i < names.Length; i++)
        {
            int cat = i;
            var button = new AntButton { Text = names[i], Font = new Font(Font.FontFamily, 12, FontStyle.Bold), Width = 135, Height = 60 };
            button.Click += delegate { selected = cat; RefreshChoices(); };
            var catCard = new Panel { Width = 138, Height = 60 };
            var avatar = new PictureBox { Width = 42, Height = 42, Left = 5, Top = 8, SizeMode = PictureBoxSizeMode.Zoom };
            using (var stream = Setup.Resource(i == defaults[0] ? "picture0" : "picture0-" + (i + 1)))
            using (var source = Image.FromStream(stream))
                avatar.Image = InstallerWindow.ResizePicture(source, 36, true);
            avatar.Disposed += delegate { avatar.Image.Dispose(); };
            avatar.Click += delegate { selected = cat; RefreshChoices(); };
            button.SetBounds(49, 5, 86, 48);
            catCard.Controls.Add(button); catCard.Controls.Add(avatar);
            cats.Controls.Add(catCard);
        }
        Controls.Add(new Label { Text = "选中小猫后点击场景分配，再点一次取消。", AutoSize = true, Left = 24, Top = 142 });
        for (int i = 0; i < Choices.Length; i++)
        {
            int index = i, x = 24 + i % 2 * 302, y = 178 + i / 2 * 69;
            var button = new AssignmentCard { Left = x, Top = y, Width = 289, Height = 60, Text = Setup.Names[i] };
            assignments[i] = button;
            button.Click += delegate { Choices[index] = Choices[index] == selected ? -1 : selected; RefreshChoices(); };
            Controls.Add(button);
        }
        var current = new AntButton { Text = "使用当前搭配", Left = 24, Top = 472, Width = 145, Height = 36 };
        current.Click += delegate { Array.Copy(defaults, Choices, Choices.Length); RefreshChoices(); };
        var cancel = new AntButton { Text = "取消", Left = 427, Top = 472, Width = 88, Height = 36, DialogResult = DialogResult.Cancel };
        apply.SetBounds(528, 472, 88, 36);
        Controls.Add(current); Controls.Add(cancel); Controls.Add(apply);
        count.SetBounds(188, 482, 220, 25); Controls.Add(count);
        CancelButton = cancel; AcceptButton = apply;
        Array.Copy(defaults, Choices, Choices.Length);
        RefreshChoices();
    }

    void RefreshChoices()
    {
        for (int i = 0; i < names.Length; i++)
        {
            var button = (AntButton)cats.Controls[i].Controls[0];
            button.SelectedStyle = selected == i; button.Invalidate();
        }
        int assigned = Array.FindAll(Choices, n => n >= 0).Length;
        count.Text = "已分配 " + assigned + " / " + Choices.Length; apply.Enabled = assigned > 0;
        for (int i = 0; i < Choices.Length; i++)
        {
            int cat = Choices[i], shown = cat < 0 ? selected : cat;
            assignments[i].Status = cat < 0 ? "保留原指针" : names[cat]; assignments[i].Assigned = cat >= 0;
            var old = assignments[i].Image;
            int variant = shown == defaults[i] ? 0 : shown + 1;
            using (var source = Setup.Resource(variant == 0 ? "picture" + i : "picture" + i + "-" + variant))
            using (var original = Image.FromStream(source))
                assignments[i].Image = InstallerWindow.ResizePicture(original, 36, true);
            assignments[i].Invalidate();
            if (old != null) old.Dispose();
        }
    }

    internal void VerifyAssignments()
    {
        if (names.Length < 2) throw new IOException("至少需要两只可选猫咪");
        for (int i = 0; i < Choices.Length; i++)
        {
            selected = (defaults[i] + 1) % names.Length;
            assignments[i].PerformClick();
            if (Choices[i] != selected) throw new IOException("猫咪分配失败");
        }
        foreach (int i in new int[] {0, 5, 6, 7})
        {
            selected = Choices[i];
            assignments[i].PerformClick();
            if (Choices[i] != -1) throw new IOException("取消分配失败");
        }
    }

    internal int[] ResourceChoices()
    {
        var result = new int[Choices.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = Choices[i] < 0 ? -1 : Choices[i] == defaults[i] ? 0 : Choices[i] + 1;
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var picture in assignments)
                if (picture != null && picture.Image != null) picture.Image.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class InstallerWindow : Form
{
    readonly Color Ink = Color.FromArgb(48, 36, 29), Muted = Color.FromArgb(113, 90, 73), Accent = Color.FromArgb(183, 139, 88);
    readonly Label title = new Label(), subtitle = new Label(), step = new Label(), percent = new Label();
    readonly Panel loading = new SmoothPanel(), scenarios = new Panel();
    readonly Button done = new AntButton { Primary = true }, restore = new AntButton();
    readonly Button closeWindow = new AntButton();
    readonly Panel sizeControls = new Panel();
    readonly AntButton[] presets = { new AntButton { Text = "小" }, new AntButton { Text = "中" }, new AntButton { Text = "大" } };
    readonly int[] presetPixels = {25, 35, 45};
    int selectedPixels = 35;
    bool previewBusy;
    string previewError;
    readonly Label sizeLabel = new Label();
    readonly Button applySize = new AntButton { Primary = true, Text = "应用大小" };
    int appliedPixels = 35;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Image runner;
    readonly float displayScale;
    readonly bool restoring;
    bool working = true;
    int phase, animation;
    float progress;
    readonly string[] messages = { "检查指针文件", "备份原来的鼠标方案", "安放两只小猫", "应用八种鼠标效果", "安装完成" };
    internal InstallerWindow(bool restoring)
    {
        this.restoring = restoring;
        Text = "喵喵就位";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        AutoScaleMode = AutoScaleMode.None;
        using (var graphics = CreateGraphics()) displayScale = graphics.DpiX / 96f;
        string previewScale = Environment.GetEnvironmentVariable("MIAOMIAO_PREVIEW_SCALE");
        if (!string.IsNullOrEmpty(previewScale)) displayScale = float.Parse(previewScale, System.Globalization.CultureInfo.InvariantCulture);
        displayScale = Math.Min(displayScale, (Screen.FromControl(this).WorkingArea.Width - 32) / 660f);
        displayScale = Math.Min(displayScale, (Screen.FromControl(this).WorkingArea.Height - 32) / 754f);
        Font = new Font("Microsoft YaHei", 10);
        BackColor = Color.FromArgb(255, 248, 239); ForeColor = Ink;
        ClientSize = new Size(660, 424); FormBorderStyle = FormBorderStyle.None;
        AutoScroll = false;
        MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        title.SetBounds(30, 28, 600, 40); title.Font = new Font(Font.FontFamily, 19, FontStyle.Bold);
        title.Text = restoring ? "小猫正在交接…" : "小猫正在赶来…";
        subtitle.SetBounds(32, 76, 600, 32); subtitle.ForeColor = Muted;
        subtitle.Text = restoring ? "正在恢复安装前的鼠标方案" : "正在为你准备八种鼠标效果";
        Controls.Add(title); Controls.Add(subtitle);
        using (var stream = Setup.Resource("runner"))
        using (var original = Image.FromStream(stream)) runner = ResizePicture(original, D(112));
        loading.SetBounds(32, 116, 596, 150); loading.Paint += DrawLoading; Controls.Add(loading);
        step.SetBounds(32, 272, 440, 28); step.ForeColor = Muted; Controls.Add(step);
        percent.SetBounds(545, 272, 80, 28); percent.TextAlign = ContentAlignment.TopRight; percent.ForeColor = Muted; Controls.Add(percent);
        done.Text = "完成"; done.SetBounds(518, 564, 108, 40); done.Click += delegate { Close(); }; done.Visible = false; Controls.Add(done);
        restore.Text = "恢复原鼠标"; restore.SetBounds(366, 564, 140, 40); restore.Visible = false;
        restore.Click += delegate {
            try { Setup.Restore(); scenarios.Visible = false; title.Text = "原来的鼠标已回来"; subtitle.Text = "已恢复首次安装前的鼠标方案。"; restore.Visible = false; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "恢复未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }; Controls.Add(restore);
        scenarios.SetBounds(30, 128, 600, 408); scenarios.Visible = false; Controls.Add(scenarios);
        foreach (Control control in Controls) control.Top += 64;
        var header = new BrandHeader { Dock = DockStyle.Top, Width = 660, Height = 64 };
        var avatar = new PictureBox { Image = BrandAvatar(), SizeMode = PictureBoxSizeMode.Zoom };
        avatar.SetBounds(27, 5, 58, 58); header.Controls.Add(avatar);
        avatar.Disposed += delegate { avatar.Image.Dispose(); };
        var brand = new Label { Text = "喵喵就位", AutoSize = false, BackColor = Color.Transparent, Font = new Font(Font.FontFamily, 17, FontStyle.Bold) };
        brand.SetBounds(115, 13, 280, 42); header.Controls.Add(brand);
        closeWindow.Text = "×"; closeWindow.SetBounds(598, 15, 36, 34); closeWindow.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        closeWindow.Click += delegate { Close(); }; header.Controls.Add(closeWindow);
        header.MouseDown += DragWindow; brand.MouseDown += DragWindow;
        Controls.Add(header);
        timer.Interval = 35; timer.Tick += Tick;
        Shown += delegate { if (working) timer.Start(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (working) e.Cancel = true; };
        Scale(new SizeF(displayScale, displayScale));
        Font = new Font("Microsoft YaHei", 10.5f * displayScale);
        title.Font = new Font(Font.FontFamily, 19.5f * displayScale, FontStyle.Bold);
        brand.Font = new Font(Font.FontFamily, 21 * displayScale, FontStyle.Bold);
        sizeControls.SetBounds(D(30), D(173), D(600), D(52));
        sizeLabel.SetBounds(0, D(13), D(145), D(28));
        applySize.SetBounds(D(465), D(7), D(135), D(37));
        sizeControls.Controls.Add(sizeLabel); sizeControls.Controls.Add(applySize);
        for (int i = 0; i < presets.Length; i++)
        {
            int chosenPreset = i;
            presets[i].SetBounds(D(145 + i * 96), D(7), D(86), D(37));
            presets[i].Click += delegate
            {
                if (previewBusy || selectedPixels == presetPixels[chosenPreset]) return;
                previewBusy = true; applySize.Enabled = false;
                foreach (var button in presets) button.Enabled = false;
                subtitle.Text = "正在准备“" + presets[chosenPreset].Text + "”尺寸预览…";
                int size = presetPixels[chosenPreset];
                try
                {
                    Setup.PrepareSize(size);
                    selectedPixels = size; RebuildSizePreview();
                    previewError = null;
                }
                catch (Exception error) { previewError = error.ToString(); if (!Setup.SelfTesting) MessageBox.Show(this, error.Message, "预览未完成"); }
                finally
                {
                    previewBusy = false;
                    foreach (var button in presets) button.Enabled = true;
                    RefreshPresets();
                }
            };
            sizeControls.Controls.Add(presets[i]);
        }
        sizeControls.Visible = false; Controls.Add(sizeControls);
        applySize.Click += delegate
        {
            try { Setup.ApplySize(selectedPixels); appliedPixels = selectedPixels; RefreshPresets(); subtitle.Text = "大小已应用。移到下方区域试用鼠标效果。"; }
            catch (Exception error) { MessageBox.Show(this, error.Message, "大小应用未完成"); }
        };
    }
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    void DragWindow(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 40 || Height < 40) return;
        using (var path = AntShape.Rounded(new RectangleF(0, 0, Width, Height), 18 * Math.Max(1, displayScale)))
        { var old = Region; Region = new Region(path); if (old != null) old.Dispose(); }
    }
    int D(int value) { return (int)Math.Round(value * displayScale); }
    Image BrandAvatar()
    {
        using (var stream = Setup.Resource("brand"))
        using (var source = Image.FromStream(stream))
        {
            return ResizePicture(source, 128);
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!scenarios.Visible) return;
        using (var pen = new Pen(Color.FromArgb(229, 210, 190))) e.Graphics.DrawLine(pen, D(30), done.Top - D(18), ClientSize.Width - D(30), done.Top - D(18));
    }
    internal static Image ResizePicture(Image original, int size, bool trimTransparent = false)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var attributes = new System.Drawing.Imaging.ImageAttributes())
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            attributes.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
            Rectangle bounds = new Rectangle(0, 0, original.Width, original.Height);
            if (trimTransparent)
            {
                using (var pixels = new Bitmap(original))
                {
                    int left = pixels.Width, top = pixels.Height, right = -1, bottom = -1;
                    for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
                    {
                        if (pixels.GetPixel(x, y).A < 80) continue;
                        left = Math.Min(left, x); right = Math.Max(right, x);
                        top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    }
                    if (right >= left) bounds = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
                }
            }
            float scale = Math.Min((float)size / bounds.Width, (float)size / bounds.Height);
            int width = (int)Math.Round(bounds.Width * scale), height = (int)Math.Round(bounds.Height * scale);
            graphics.DrawImage(original, new Rectangle((size - width) / 2, (size - height) / 2, width, height), bounds.X, bounds.Y, bounds.Width, bounds.Height, GraphicsUnit.Pixel, attributes);
        }
        return bitmap;
    }
    void DrawLoading(object sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float x = D(56) + progress / 100 * D(484);
        int bounce = working ? (int)(Math.Sin(animation * .65) * D(4)) : 0;
        e.Graphics.DrawImageUnscaled(runner, (int)x - D(56), D(20) + bounce);
        using (var track = new SolidBrush(Color.FromArgb(237, 226, 212))) e.Graphics.FillRectangle(track, D(38), D(107), D(520), D(8));
        using (var fill = new SolidBrush(Accent)) e.Graphics.FillRectangle(fill, D(38), D(107), D(520) * progress / 100, D(8));
    }
    void Tick(object sender, EventArgs e)
    {
        animation++;
        float target = phase * 25;
        progress = Math.Min(target, progress + 2);
        percent.Text = ((int)progress) + "%";
        loading.Invalidate();
        if (animation % 16 != 0) return;
        try
        {
            if (restoring)
            {
                Setup.Restore(); FinishRestore(); return;
            }
            step.Text = messages[Math.Min(phase, 4)];
            switch (phase)
            {
                case 0: Setup.Prepare(); break;
                case 1: Setup.SaveBackup(); break;
                case 2: Setup.CopyCursors(); break;
                case 3: Setup.Apply(); break;
                default: Finish(); return;
            }
            phase++;
        }
        catch (Exception ex)
        {
            timer.Stop(); working = false;
            string rollback = "";
            if (Setup.Changed)
            {
                try { Setup.Restore(); rollback = "\n已恢复安装前的鼠标设置。"; }
                catch (Exception recovery) { rollback = "\n恢复也未完成：" + recovery.Message; }
            }
            title.Text = "小猫暂时没能就位"; subtitle.Text = "请查看下面的原因，再重新运行。";
            step.Text = ex.Message + rollback; step.Height = D(90); step.Width = D(590);
            ClientSize = new Size(D(660), D(504)); done.SetBounds(D(518), D(444), D(108), D(40)); done.Visible = true;
        }
    }
    void FinishRestore()
    {
        timer.Stop(); working = false; loading.Visible = false; step.Visible = false; percent.Visible = false;
        title.Text = "原来的鼠标已回来"; subtitle.Text = "已恢复首次安装前的鼠标方案。";
        done.SetBounds(D(518), D(334), D(108), D(40)); done.Visible = true;
    }
    void Finish()
    {
        timer.Stop(); working = false; loading.Visible = false; step.Visible = false; percent.Visible = false;

        title.Text = "已完成，请试用鼠标效果";
        subtitle.Text = "把鼠标移到下面的区域，看看两只小猫的表现。";
        BuildScenarios();
        int rows = (scenarios.Controls.Count + 1) / 2;
        scenarios.Top = D(256);
        scenarios.Height = D((rows - 1) * 100 + 90);
        done.Top = restore.Top = scenarios.Bottom + D(48);
        ClientSize = new Size(D(660), done.Bottom + D(20));
        scenarios.Visible = true; done.Visible = true; restore.Visible = true;

        appliedPixels = Setup.PreviewPixels; selectedPixels = appliedPixels;
        RefreshPresets(); sizeControls.Visible = true;
    }
    void RefreshPresets()
    {
        sizeLabel.Text = "指针大小";
        for (int i = 0; i < presets.Length; i++) { presets[i].SelectedStyle = selectedPixels == presetPixels[i]; presets[i].Invalidate(); }
        applySize.Enabled = !previewBusy && selectedPixels != appliedPixels;
    }
    void RebuildSizePreview()
    {
            while (scenarios.Controls.Count > 0) scenarios.Controls[0].Dispose();
            scenarios.Controls.Clear(); BuildScenarios();
            subtitle.Text = "当前为试用大小，点击“应用大小”后更新系统鼠标。";
    }
    internal void Preview() { Finish(); }
    internal void VerifySizePreviews()
    {
        foreach (int index in new int[] {0, 2, 1})
        {
            presets[index].PerformClick();
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (previewBusy && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            if (previewError != null) throw new IOException(previewError);
            if (previewBusy || Setup.PreviewPixels != presetPixels[index] || scenarios.Controls.Count == 0) throw new IOException("尺寸选项预览检查失败：期望" + presetPixels[index] + "，实际" + Setup.PreviewPixels + "，忙碌" + previewBusy + "，卡片" + scenarios.Controls.Count);
            foreach (Control card in scenarios.Controls)
            {
                int cursorIndex = (int)card.Tag;
                Setup.VerifyTrialCursor(card.Cursor.Handle, Setup.CursorPath(cursorIndex));
                var picture = (CursorPixelPreview)card.Controls[0];
                if (Math.Abs(Math.Max(picture.Image.Width, picture.Image.Height) - Setup.PreviewPixels) > 3) throw new IOException("预览图案大小异常");
                picture.VerifyCentered();
            }
            using (var image = new Bitmap(Width, Height))
            { DrawToBitmap(image, new Rectangle(Point.Empty, Size)); image.Save(Path.Combine(Setup.Root, "preview-" + Setup.PreviewPixels + ".png")); }
        }
        File.WriteAllText(Path.Combine(Setup.Root, "display-check.txt"), "UI thread cursor handles, pixels and hotspots match all selected size previews. Card centering verified. Fixed-pixel cursor creation: " + Setup.FixedPixelScalingAvailable + ". This does not measure the final desktop compositor output.");
    }
    internal void PreviewLoading() { working = false; progress = 55; step.Text = "安放两只小猫"; percent.Text = "55%"; }
    void BuildScenarios()
    {
        int cardIndex = 0;
        string[] descriptions = { "在空白区域移动鼠标", "在这里输入文字", "试试这个链接", "此操作暂不可用", "等待任务处理完成", "按住拖动这只小猫", "左右移动鼠标试用", "上下移动鼠标试用" };
        for (int i = 0; i < 8; i++)
        {
            int index = i;
            if (!Setup.Enabled(i)) continue;
            var card = new ThemeCard { BackColor = Color.FromArgb(255, 252, 247) };
            card.Tag = i;
            card.SetBounds(D(cardIndex % 2 * 306), D(cardIndex++ / 2 * 100), D(294), D(90));
            IntPtr handle = Setup.LoadPreviewCursor(Setup.CursorPath(i));
            if (handle == IntPtr.Zero) throw new IOException("无法加载试用指针");
            var cursor = new Cursor(handle); card.Cursor = cursor;
            card.Disposed += delegate { cursor.Dispose(); Setup.DestroyCursor(handle); };
            Image thumbnail = CursorEncoder.ReadPixels(Setup.CursorPath(i));
            var picture = new CursorPixelPreview { Image = thumbnail, Cursor = cursor };
            picture.SetBounds(D(10), D(6), D(82), D(82)); card.Controls.Add(picture);
            picture.Disposed += delegate { picture.Image.Dispose(); };
            var label = new Label { Text = Setup.Names[i], AutoSize = true, Font = new Font(Font.FontFamily, 12 * displayScale, FontStyle.Bold), Cursor = cursor };
            label.SetBounds(D(117), D(16), D(170), D(25)); card.Controls.Add(label);
            var description = new Label { Text = descriptions[i], ForeColor = Muted, Cursor = cursor };
            description.SetBounds(D(117), D(48), D(170), D(32)); card.Controls.Add(description);
            if (index == 1)
            {
                description.Visible = false;
                var input = new AntInput { Cursor = cursor };
                input.SetBounds(D(115), D(44), D(169), D(34)); card.Controls.Add(input);
            }
            if (index == 2)
            {
                description.Click += delegate { description.Text = "猫爪点击成功！"; };
            }
            if (index == 5)
            {
                Point anchor = Point.Empty; bool dragging = false;
                picture.MouseDown += delegate(object s, MouseEventArgs e) { dragging = true; anchor = e.Location; picture.Capture = true; };
                picture.MouseMove += delegate(object s, MouseEventArgs e) { if (dragging) { picture.Left = Math.Max(0, Math.Min(card.Width - picture.Width, picture.Left + e.X - anchor.X)); picture.Top = Math.Max(0, Math.Min(card.Height - picture.Height, picture.Top + e.Y - anchor.Y)); } };
                picture.MouseUp += delegate { dragging = false; picture.Capture = false; };
            }
            scenarios.Controls.Add(card);
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); runner.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class SmoothPanel : Panel
{
    internal SmoothPanel() { DoubleBuffered = true; ResizeRedraw = true; }
}

internal sealed class CursorPixelPreview : Control
{
    internal Image Image;
    internal CursorPixelPreview()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent.BackColor);
        if (Image == null) return;
        e.Graphics.DrawImageUnscaled(Image, (ClientSize.Width - Image.Width) / 2, (ClientSize.Height - Image.Height) / 2);
    }
    internal void VerifyCentered()
    {
        // Check the painted result, not just the control rectangle.
        using (var bitmap = new Bitmap(Width, Height))
        {
            DrawToBitmap(bitmap, ClientRectangle);
            int left = Width, top = Height, right = -1, bottom = -1;
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            {
                Color c = bitmap.GetPixel(x, y), bg = Parent.BackColor;
                if (Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) < 30) continue;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            if (right < left || Math.Abs(left - (Width - 1 - right)) > 3 || Math.Abs(top - (Height - 1 - bottom)) > 3)
                throw new IOException("卡片图案没有居中");
        }
    }
}

internal sealed class BrandHeader : Panel
{
    internal BrandHeader() { DoubleBuffered = true; BackColor = Color.FromArgb(248, 226, 199); }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(ClientRectangle, Color.FromArgb(248, 222, 190), Color.FromArgb(253, 239, 220), 0f)) e.Graphics.FillRectangle(fill, ClientRectangle);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float scale = Height / 64f;
        g.ScaleTransform(scale, scale);
        float width = Width / scale;
        using (var tint = new SolidBrush(Color.FromArgb(65, 210, 156, 99)))
        {
            for (int row = 0; row < 3; row++) for (int col = 0; col < 9; col++)
                if ((row + col) % 2 == 0) g.FillRectangle(tint, width - 230 + col * 15, -5 + row * 15, 15, 15);
            g.FillEllipse(tint, width - 234, 40, 23, 17);
            g.FillEllipse(tint, width - 242, 30, 7, 9); g.FillEllipse(tint, width - 232, 25, 7, 9);
            g.FillEllipse(tint, width - 221, 25, 7, 9); g.FillEllipse(tint, width - 211, 30, 7, 9);
        }
        using (var pen = new Pen(Color.FromArgb(199, 150, 103), 1))
        {
            g.DrawLine(pen, 100, 17, 100, 47);
            pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
            g.DrawBezier(pen, width - 310, 62, width - 245, -10, width - 190, 73, width - 90, 0);
        }
        g.ResetTransform();
    }
}

internal sealed class ThemeCard : Panel
{
    internal ThemeCard() { DoubleBuffered = true; }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width < 30 || Height < 30) return;
        using (var path = AntShape.Rounded(new RectangleF(0, 0, Width, Height), Height * .12f))
        { var old = Region; Region = new Region(path); if (old != null) old.Dispose(); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = AntShape.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), Height * .12f))
        using (var pen = new Pen(Color.FromArgb(229, 210, 190))) e.Graphics.DrawPath(pen, path);
        using (var pen = new Pen(Color.FromArgb(225, 199, 170))) e.Graphics.DrawLine(pen, Width * .355f, Height * .2f, Width * .355f, Height * .8f);
    }
}

internal static class AntShape
{
    internal static System.Drawing.Drawing2D.GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
}

// Native controls styled to match Ant Design's rounded primary/default buttons.
internal sealed class AssignmentCard : Button
{
    internal string Status;
    internal bool Assigned;
    bool hovered;

    internal AssignmentCard()
    {
        FlatStyle = FlatStyle.Flat;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent.BackColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var outline = AntShape.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), 11))
        using (var fill = new SolidBrush(hovered ? Color.FromArgb(250, 234, 213) : Color.FromArgb(255, 252, 247)))
        using (var stroke = new Pen(hovered || Focused ? Color.FromArgb(152, 80, 31) : Color.FromArgb(229, 210, 190)))
        { e.Graphics.FillPath(fill, outline); e.Graphics.DrawPath(stroke, outline); }
        if (Image != null) e.Graphics.DrawImageUnscaled(Image, 13, (Height - Image.Height) / 2);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(64, 0, 110, Height), Color.FromArgb(48, 36, 29), TextFormatFlags.VerticalCenter);
        var badge = new Rectangle(177, 17, Width - 190, 26);
        using (var pill = AntShape.Rounded(badge, 13))
        using (var fill = new SolidBrush(Assigned ? Color.FromArgb(249, 226, 199) : Color.FromArgb(242, 236, 227)))
            e.Graphics.FillPath(fill, pill);
        TextRenderer.DrawText(e.Graphics, Status, Font, badge, Color.FromArgb(121, 67, 24),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class AntButton : Button
{
    internal bool Primary, SelectedStyle;
    bool hovered, pressed;
    internal AntButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        UseVisualStyleBackColor = false; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent.BackColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float unit = Height / 37f;
        Color blue = Color.FromArgb(152, 80, 31), hover = Color.FromArgb(134, 69, 25), active = Color.FromArgb(143, 80, 34);
        Color state = pressed ? active : hovered ? hover : blue;
        if (!Enabled) state = Color.FromArgb(205, 184, 163);
        var rectangle = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var shape = AntShape.Rounded(rectangle, 6 * unit))
        using (Brush brush = new SolidBrush(Primary ? state : SelectedStyle ? Color.FromArgb(249, 226, 199) : Color.FromArgb(255, 252, 247)))
        using (var pen = new Pen(Primary || SelectedStyle || hovered || pressed ? state : Color.FromArgb(229, 210, 190), Math.Max(1, unit)))
        {
            e.Graphics.FillPath(brush, shape); e.Graphics.DrawPath(pen, shape);
        }
        if (Focused && ShowFocusCues)
        {
            using (var shape = AntShape.Rounded(new RectangleF(3, 3, Width - 7, Height - 7), 4 * unit))
            using (var pen = new Pen(Primary ? Color.White : blue)) { pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot; e.Graphics.DrawPath(pen, shape); }
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Primary ? Color.White : SelectedStyle ? Color.FromArgb(121, 67, 24) : Color.FromArgb(48, 36, 29), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class AntInput : UserControl
{
    readonly TextBox editor = new TextBox { BorderStyle = BorderStyle.None, BackColor = Color.White };
    bool hovered;
    const string Hint = "试着输入几个字";
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, string text);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    static extern IntPtr ReadHint(IntPtr handle, uint message, System.Text.StringBuilder text, IntPtr capacity);
    internal static void VerifyBehavior()
    {
        using (var input = new AntInput())
        {
            IntPtr handle = input.editor.Handle;
            input.SetHover(false);
            var text = new System.Text.StringBuilder(100);
            ReadHint(handle, 0x1502, text, new IntPtr(100));
            if (text.ToString() != Hint) throw new IOException("输入提示未显示");
            input.SetHover(true); text.Clear(); ReadHint(handle, 0x1502, text, new IntPtr(100));
            if (text.Length != 0) throw new IOException("移入后提示未隐藏");
            input.editor.Text = "保留我输入的文字";
            input.SetHover(false); input.SetHover(true);
            if (input.editor.Text != "保留我输入的文字") throw new IOException("输入文字被修改");
        }
    }
    internal AntInput()
    {
        DoubleBuffered = true; BackColor = Color.White;
        Controls.Add(editor);
        editor.HandleCreated += delegate { UpdateHint(); };
        editor.Enter += delegate { UpdateHint(); Invalidate(); };
        editor.Leave += delegate { UpdateHint(); Invalidate(); };
        editor.MouseEnter += delegate { SetHover(true); };
        editor.MouseLeave += delegate { SetHover(ClientRectangle.Contains(PointToClient(System.Windows.Forms.Cursor.Position))); };
        MouseEnter += delegate { SetHover(true); };
        MouseLeave += delegate { SetHover(ClientRectangle.Contains(PointToClient(System.Windows.Forms.Cursor.Position))); };
        MouseDown += delegate { editor.Focus(); };
    }
    void SetHover(bool value) { hovered = value; UpdateHint(); Invalidate(); }
    void UpdateHint()
    {
        if (editor.IsHandleCreated) SendMessage(editor.Handle, 0x1501, IntPtr.Zero, hovered || editor.Focused ? "" : Hint);
    }
    protected override void OnCursorChanged(EventArgs e) { base.OnCursorChanged(e); editor.Cursor = Cursor; }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int padding = Math.Max(8, (int)Math.Round(Height * .3));
        editor.SetBounds(padding, Math.Max(2, (Height - editor.PreferredHeight) / 2), Math.Max(1, Width - 2 * padding), editor.PreferredHeight);
        Invalidate();
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); OnResize(EventArgs.Empty); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float unit = Height / 34f;
        using (var path = AntShape.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), 6 * unit))
        using (var brush = new SolidBrush(Color.White))
        using (var pen = new Pen(editor.Focused ? Color.FromArgb(174,105,49) : hovered ? Color.FromArgb(192,125,66) : Color.FromArgb(217,217,217), Math.Max(1, unit)))
        { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
    }
}
