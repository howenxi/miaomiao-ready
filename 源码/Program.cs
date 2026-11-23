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
            do {
                Setup.ReturnToPicker = false;
                if (!restoring && !Setup.ChooseCats()) return;
                Application.Run(new InstallerWindow(restoring));
            } while (!restoring && Setup.ReturnToPicker);
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
    internal static int RequestedPixels = 35;
    internal static bool ReturnToPicker;
    static int[] acceptedCats;

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
                    using(var framed=WindowShadow.Render(dialog.Size,40))
                    {
                        if(framed.GetPixel(40+dialog.Width/2,40+dialog.Height/2).A!=0) throw new IOException("阴影遮挡窗口内容");
                        using(var g=Graphics.FromImage(framed))
                        using(var clip=AntShape.Rounded(new RectangleF(40,40,dialog.Width,dialog.Height),20*dialog.Width/850f))
                        { g.SetClip(clip); g.DrawImageUnscaled(image,40,40); }
                        using(var canvas=new Bitmap(framed.Width,framed.Height))
                        using(var g=Graphics.FromImage(canvas))
                        { g.Clear(Color.FromArgb(241,238,246)); g.DrawImageUnscaled(framed,0,0); canvas.Save(Path.Combine(output,"cat-picker-shadow.png")); }
                    }
                    if(dialog.Icon==null) throw new IOException("缺少猫咪任务栏图标");
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
            using (var dialog = new CatPicker(cats, acceptedCats, RequestedPixels))
            {
                if (dialog.ShowDialog() != DialogResult.OK) return false;
                Array.Copy(dialog.ResourceChoices(), choices, choices.Length);
                acceptedCats = (int[])dialog.Choices.Clone(); RequestedPixels = dialog.ChosenPixels;
                SizedFolder = null; variantMode = true;
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

internal class PrototypeWindow : Form
{
    WindowShadow shadow;
    [StructLayout(LayoutKind.Sequential)] struct FrameMargins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref FrameMargins margins);
    protected PrototypeWindow()
    {
        DoubleBuffered = true; ResizeRedraw = true; Padding = new Padding(1);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Shown += delegate { shadow = new WindowShadow(this); shadow.Show(this); shadow.RefreshShadow(); };
        LocationChanged += delegate { if (shadow != null) shadow.RefreshShadow(); };
        SizeChanged += delegate { if (shadow != null) { shadow.Visible = WindowState != FormWindowState.Minimized; shadow.RefreshShadow(); } };
        VisibleChanged += delegate { if (shadow != null) shadow.Visible = Visible && WindowState != FormWindowState.Minimized; };
        FormClosed += delegate { if (shadow != null) { shadow.Dispose(); shadow = null; } };
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { var margins = new FrameMargins { Left=1, Right=1, Top=1, Bottom=1 }; DwmExtendFrameIntoClientArea(Handle, ref margins); } catch (DllNotFoundException) { }
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e); if(ClientSize.Width<40||ClientSize.Height<40)return;
        using(var path=AntShape.Rounded(new RectangleF(0,0,ClientSize.Width,ClientSize.Height),20*ClientSize.Width/850f))
        {var old=Region;Region=new Region(path);if(old!=null)old.Dispose();}
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = ClientSize.Width / 850f;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = AntShape.Rounded(new RectangleF(.5f,.5f,ClientSize.Width-1,ClientSize.Height-1),20*scale))
        using (var pen = new Pen(Color.FromArgb(230,223,237),Math.Max(1,scale))) e.Graphics.DrawPath(pen,path);
    }
}

// Click-through owned layer keeps the soft exterior shadow independent of Windows theme settings.
internal sealed class WindowShadow : Form
{
    readonly Form target;
    [StructLayout(LayoutKind.Sequential)] struct PointI { public int X,Y; public PointI(int x,int y) { X=x; Y=y; } }
    [StructLayout(LayoutKind.Sequential)] struct SizeI { public int Width,Height; public SizeI(int w,int h) { Width=w; Height=h; } }
    [StructLayout(LayoutKind.Sequential, Pack=1)] struct Blend { public byte Operation,Flags,Alpha,Format; }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr window,IntPtr screen,ref PointI destination,ref SizeI size,IntPtr memory,ref PointI source,int key,ref Blend blend,int flags);
    internal WindowShadow(Form target) { this.target=target; FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; StartPosition=FormStartPosition.Manual; }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var p=base.CreateParams; p.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000; return p; } }
    internal static Bitmap Render(Size windowSize, int padding)
    {
        var bitmap=new Bitmap(windowSize.Width+padding*2,windowSize.Height+padding*2,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        float scale=windowSize.Width/850f;
        using(var g=Graphics.FromImage(bitmap))
        {
            g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            for(int spread=padding-2; spread>=1; spread--)
            {
                float extent=spread;
                using(var path=AntShape.Rounded(new RectangleF(padding-extent,padding-extent+8*scale,windowSize.Width+extent*2,windowSize.Height+extent*2),20*scale+extent))
                using(var brush=new SolidBrush(Color.FromArgb(1,0,0,0))) g.FillPath(brush,path);
            }
            g.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using(var path=AntShape.Rounded(new RectangleF(padding,padding,windowSize.Width,windowSize.Height),20*scale))
            using(var brush=new SolidBrush(Color.Transparent)) g.FillPath(brush,path);
        }
        return bitmap;
    }
    internal void RefreshShadow()
    {
        if(IsDisposed || target.IsDisposed || !target.Visible) return;
        int padding=Math.Max(24,(int)Math.Round(32*target.Width/850f));
        using(var bitmap=Render(target.Size,padding))
        {
            IntPtr screen=GetDC(IntPtr.Zero), memory=CreateCompatibleDC(screen), image=bitmap.GetHbitmap(Color.FromArgb(0)), previous=SelectObject(memory,image);
            try {
                var destination=new PointI(target.Left-padding,target.Top-padding); var size=new SizeI(bitmap.Width,bitmap.Height); var source=new PointI(0,0); var blend=new Blend { Alpha=255,Format=1 };
                if(!UpdateLayeredWindow(Handle,screen,ref destination,ref size,memory,ref source,0,ref blend,2)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { SelectObject(memory,previous); DeleteObject(image); DeleteDC(memory); ReleaseDC(IntPtr.Zero,screen); }
        }
    }
}

internal sealed class CatPicker : PrototypeWindow
{
    internal readonly int[] Choices = new int[8];
    internal int ChosenPixels;
    readonly AssignmentCard[] assignments = new AssignmentCard[8];
    readonly int[] previewKeys = {-1,-1,-1,-1,-1,-1,-1,-1};
    readonly string[] names;
    readonly int[] defaults;
    readonly FlowLayoutPanel cats = new FlowLayoutPanel();
    readonly AntButton[] catButtons;
    readonly AntButton[] sizes = new AntButton[3];
    readonly int[] pixels = {25,35,45};
    readonly AntButton apply = new AntButton { Text="应用指针", Primary=true, DialogResult=DialogResult.OK };
    readonly AntButton applyAll = new AntButton { Quiet=true };
    int selected;
    readonly float scale;
    internal CatPicker(XDocument catalog) : this(catalog,null,35) { }
    internal CatPicker(XDocument catalog, int[] initial, int size)
    {
        var entries = new System.Collections.Generic.List<XElement>(catalog.Root.Elements("cat"));
        names=entries.ConvertAll(e=>(string)e.Attribute("name")).ToArray(); catButtons=new AntButton[names.Length];
        defaults=Array.ConvertAll(((string)catalog.Root.Attribute("defaults")).Split(','),int.Parse);
        Array.Copy(initial ?? defaults,Choices,8); ChosenPixels=size;
        Text="喵喵就位 · 选择小猫"; AutoScaleMode=AutoScaleMode.None;
        Font=new Font("Microsoft YaHei",11.25f); BackColor=Color.White; ForeColor=Color.FromArgb(41,36,57);
        StartPosition=FormStartPosition.CenterScreen; FormBorderStyle=FormBorderStyle.None; MaximizeBox=false; MinimizeBox=false;
        ClientSize=new Size(850,560); scale=InstallerWindow.WindowScale(this);
        cats.SetBounds(36,94,776,56); cats.AutoScroll=false; cats.WrapContents=false; cats.Margin=Padding.Empty; cats.Padding=Padding.Empty; Controls.Add(cats);
        for(int i=0;i<names.Length;i++)
        {
            int c=i; var button=new AntButton {Text=names[i],CatStyle=true,Font=new Font(Font.FontFamily,11.25f,FontStyle.Bold),Width=(names[i]=="银渐层"||names[i]=="狸花猫")?121:106,Height=56,Margin=new Padding(0,0,10,0)};
            button.Click+=delegate {selected=c;RefreshChoices();};
            using(var stream=Setup.Resource(i==defaults[0]?"picture0":"picture0-"+(i+1)))
            using(var image=Image.FromStream(stream)) button.Image=InstallerWindow.ResizePicture(image,(int)Math.Round(34*scale),true);
            button.Disposed+=delegate {if(button.Image!=null)button.Image.Dispose();}; catButtons[i]=button;cats.Controls.Add(button);
        }
        Controls.Add(new Label {Text="选小猫，点指针搭配",AutoSize=true,Left=36,Top=174,Font=new Font(Font.FontFamily,9.75f),ForeColor=Color.FromArgb(102,93,116)});
        applyAll.SetBounds(684,164,128,34);applyAll.Font=new Font(Font.FontFamily,9.75f,FontStyle.Bold);
        applyAll.Click+=delegate {for(int i=0;i<8;i++)Choices[i]=selected;RefreshChoices();};Controls.Add(applyAll);
        Controls.Add(new Panel {Left=36,Top=205,Width=776,Height=1,BackColor=Color.FromArgb(230,223,237)});
        for(int i=0;i<8;i++)
        {
            int index=i;var row=new AssignmentCard {Left=36+i%2*400,Top=206+i/2*65,Width=376,Height=65,Text=Setup.Names[i],Font=new Font(Font.FontFamily,10.5f,FontStyle.Bold)};
            row.Click+=delegate {Choices[index]=Choices[index]==selected?-1:selected;RefreshChoices();};assignments[i]=row;Controls.Add(row);
        }
        Controls.Add(new Label {Text="指针大小",AutoSize=true,Left=36,Top=508,Font=new Font(Font.FontFamily,9.75f),ForeColor=Color.FromArgb(102,93,116)});
        var segment=new SegmentPanel();segment.SetBounds(102,498,147,38);Controls.Add(segment);
        for(int i=0;i<3;i++)
        {
            int index=i;var button=new AntButton {Text=new[]{"小","中","大"}[i],Segment=true,Font=new Font(Font.FontFamily,9.75f)};
            button.SetBounds(3+i*47,3,44,32);button.Click+=delegate {ChosenPixels=pixels[index];RefreshChoices();};sizes[i]=button;segment.Controls.Add(button);
        }
        apply.SetBounds(720,496,92,42); apply.Font=new Font(Font.FontFamily,11.25f,FontStyle.Bold); Controls.Add(apply);AcceptButton=apply;
        var brand=InstallerWindow.AddBrandHeader(this);Scale(new SizeF(scale,scale));brand.Font=new Font(Font.FontFamily,13.5f,FontStyle.Bold);
        RefreshChoices();
    }
    void RefreshChoices()
    {
        for(int i=0;i<catButtons.Length;i++){catButtons[i].SelectedStyle=selected==i;catButtons[i].Invalidate();}
        bool all=Array.TrueForAll(Choices,c=>c==selected);applyAll.Text=all?"已全部使用 ✓":"全部用"+names[selected];applyAll.Enabled=!all;applyAll.Font=new Font(Font.FontFamily,9.75f,all?FontStyle.Regular:FontStyle.Bold);
        applyAll.Width=TextRenderer.MeasureText(applyAll.Text,applyAll.Font,Size.Empty,TextFormatFlags.NoPadding).Width+(int)Math.Round(26*scale);applyAll.Left=(int)Math.Round(812*scale)-applyAll.Width;
        apply.Enabled=Array.Exists(Choices,c=>c>=0);
        for(int i=0;i<3;i++){sizes[i].SelectedStyle=ChosenPixels==pixels[i];sizes[i].Font=new Font(Font.FontFamily,9.75f,sizes[i].SelectedStyle?FontStyle.Bold:FontStyle.Regular);sizes[i].Invalidate();}
        for(int i=0;i<8;i++)
        {
            int c=Choices[i],shown=c<0?selected:c;assignments[i].Status=c<0?"保留原指针":names[c];assignments[i].Assigned=c>=0;
            int variant=shown==defaults[i]?0:shown+1;int key=variant*100+ChosenPixels;
            if(previewKeys[i]==key){assignments[i].Invalidate();continue;}
            var old=assignments[i].Image;
            using(var stream=Setup.Resource(variant==0?"picture"+i:"picture"+i+"-"+variant))
            using(var image=Image.FromStream(stream))assignments[i].Image=InstallerWindow.ResizePicture(image,ChosenPixels,true);
            previewKeys[i]=key;if(old!=null)old.Dispose();assignments[i].Invalidate();
        }
    }
    internal void VerifyAssignments()
    {
        if(Array.Exists(Choices,c=>c!=0))throw new IOException("默认应为全银渐层");
        foreach(var button in catButtons)if(button.Image.Width!=(int)Math.Round(34*scale))throw new IOException("选猫图标未按最终像素生成");
        catButtons[1].PerformClick();if(Array.Exists(Choices,c=>c!=0))throw new IOException("选猫不应改变搭配");
        assignments[1].PerformClick();assignments[2].PerformClick();
        if(Choices[0]!=0||Choices[1]!=1||Choices[2]!=1)throw new IOException("连续混搭失败");
        foreach(int i in new[]{0,2,1})
        {
            sizes[i].PerformClick();if(ChosenPixels!=pixels[i])throw new IOException("选择尺寸失败");
            foreach(var row in assignments) {if(Math.Max(row.Image.Width,row.Image.Height)!=ChosenPixels)throw new IOException("预览尺寸不一致");}
        }
        applyAll.PerformClick();if(Array.Exists(Choices,c=>c!=1)||applyAll.Enabled)throw new IOException("整套使用状态错误");
        assignments[0].PerformClick();if(Choices[0]!=-1||!applyAll.Enabled)throw new IOException("取消单项失败");
        Array.Copy(defaults,Choices,8);ChosenPixels=35;selected=0;RefreshChoices();
        using(var returned=new CatPicker(XDocument.Parse("<cats defaults='0,0,0,0,0,0,0,0'><cat name='银渐层'/><cat name='三花'/><cat name='狸花猫'/><cat name='德文'/></cats>"),new[]{0,1,0,0,0,0,0,0},45))
            if(returned.Choices[1]!=1||returned.ChosenPixels!=45)throw new IOException("返回调整丢失设置");
    }
    internal int[] ResourceChoices()
    {
        var result=new int[8];for(int i=0;i<8;i++)result[i]=Choices[i]<0?-1:Choices[i]==defaults[i]?0:Choices[i]+1;return result;
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing)foreach(var row in assignments)if(row!=null&&row.Image!=null)row.Image.Dispose();base.Dispose(disposing);
    }
}
internal sealed class SegmentPanel : Panel
{
    internal SegmentPanel(){DoubleBuffered=true;BackColor=Color.White;}
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.White);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using(var path=AntShape.Rounded(new RectangleF(0,0,Width,Height),10*Height/38f))
        using(var brush=new SolidBrush(Color.FromArgb(241,239,244)))e.Graphics.FillPath(brush,path);
    }
}

internal sealed class InstallerWindow : PrototypeWindow
{
    readonly Color Ink = Color.FromArgb(41, 36, 57), Muted = Color.FromArgb(102, 93, 116), Accent = Color.FromArgb(101, 65, 138);
    readonly Label title = new Label(), subtitle = new Label(), step = new Label(), percent = new Label();
    readonly Panel loading = new SmoothPanel(), scenarios = new Panel();
    readonly Button done = new AntButton { Primary = true }, back = new AntButton { Plain = true, Leading = true };

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
        displayScale = WindowScale(this);
        Font = new Font("Microsoft YaHei", 10);
        BackColor = Color.FromArgb(255, 255, 255); ForeColor = Ink;
        ClientSize = new Size(850, 504); FormBorderStyle = FormBorderStyle.None;
        AutoScroll = false;
        MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        title.SetBounds(36, 30, 776, 42); title.Font = new Font(Font.FontFamily, 19, FontStyle.Bold);
        title.Text = restoring ? "小猫正在交接…" : "小猫正在赶来";
        subtitle.SetBounds(36, 70, 776, 26); subtitle.ForeColor = Muted;
        subtitle.Text = restoring ? "正在恢复安装前的鼠标方案" : "正在为所选场景应用鼠标指针。";
        Controls.Add(title); Controls.Add(subtitle); subtitle.Visible=restoring;
        using (var stream = Setup.Resource("runner"))
        using (var original = Image.FromStream(stream)) runner = ResizePicture(original, D(94), true);
        loading.SetBounds(36, 123, 776, 150); loading.Paint += DrawLoading; Controls.Add(loading);
        step.SetBounds(88, 255, 440, 28); step.ForeColor = Muted; step.Font=new Font(Font.FontFamily,9.75f); Controls.Add(step);
        percent.SetBounds(680, 255, 80, 28); percent.TextAlign = ContentAlignment.TopRight; percent.ForeColor = Accent; percent.Font=new Font(Font.FontFamily,9.75f,FontStyle.Bold); Controls.Add(percent);
        done.Font=new Font(Font.FontFamily,11.25f,FontStyle.Bold); done.Text = "完成"; done.SetBounds(750, 564, 62, 42); done.Click += delegate { Close(); }; done.Visible = false; Controls.Add(done);
        back.Font=new Font(Font.FontFamily,10.5f);back.Text="← 返回调整";back.SetBounds(36,564,120,42);back.Visible=false;
        back.Click+=delegate { Setup.ReturnToPicker=true;Close(); };Controls.Add(back);
        scenarios.SetBounds(36, 184, 776, 260); scenarios.Visible = false; Controls.Add(scenarios);
        foreach (Control control in Controls) control.Top += 64;
        var brand = AddBrandHeader(this);
        var installing = new Label { Name = "InstallingStatus", Text = "正在应用", AutoSize = true, ForeColor = Muted, Left = 735, Top = 23 };
        brand.Parent.Controls.Add(installing);
        var close = Controls.Find("PrototypeClose",true); if (close.Length > 0) close[0].Visible = false;
        timer.Interval = 35; timer.Tick += Tick;
        Shown += delegate { if (working) timer.Start(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (working) e.Cancel = true; };
        Scale(new SizeF(displayScale, displayScale));
        Font = new Font("Microsoft YaHei", 11.25f);
        title.Font = new Font(Font.FontFamily, 19.5f, FontStyle.Bold);
        brand.Font = new Font(Font.FontFamily, 13.5f, FontStyle.Bold);

    }
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 40 || Height < 40) return;
        using (var path = AntShape.Rounded(new RectangleF(0, 0, Width, Height), 20 * (ClientSize.Width / 850f)))
        { var old = Region; Region = new Region(path); if (old != null) old.Dispose(); }
    }
    int D(int value) { return (int)Math.Round(value * displayScale); }
    internal static float WindowScale(Form window)
    {
        float scale;
        using (var graphics = window.CreateGraphics()) scale = graphics.DpiX / 96f;
        string previewScale = Environment.GetEnvironmentVariable("MIAOMIAO_PREVIEW_SCALE");
        if (!string.IsNullOrEmpty(previewScale)) scale = float.Parse(previewScale, System.Globalization.CultureInfo.InvariantCulture);
        var area = Screen.FromControl(window).WorkingArea;
        return Math.Min(scale, Math.Min((area.Width - 32) / 850f, (area.Height - 32) / 754f));
    }
    internal static Label AddBrandHeader(Form window)
    {
        var closeWindow = new AntButton { Plain=true };
        closeWindow.Name = "PrototypeClose";
        MouseEventHandler drag = delegate(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture(); SendMessage(window.Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
        };
        var header = new BrandHeader { Dock = DockStyle.Top, Width = 850, Height = 64 };
        var avatar = new PictureBox { Image = BrandAvatar(), SizeMode = PictureBoxSizeMode.Zoom };
        avatar.SetBounds(28, 12, 38, 38); header.Controls.Add(avatar);
        avatar.Disposed += delegate { avatar.Image.Dispose(); };
        var brand = new Label { Text = "喵喵就位", AutoSize = false, BackColor = Color.Transparent, Font = new Font(window.Font.FontFamily, 17, FontStyle.Bold) };
        brand.SetBounds(76, 18, 280, 30); header.Controls.Add(brand);
        closeWindow.Font = new Font(window.Font.FontFamily,18f); closeWindow.Text = "×"; closeWindow.SetBounds(784, 12, 38, 38); closeWindow.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        closeWindow.Click += delegate { window.Close(); }; header.Controls.Add(closeWindow);
        header.MouseDown += drag; brand.MouseDown += drag; avatar.MouseDown += drag;
        window.Controls.Add(header);
        return brand;
    }
    static Image BrandAvatar()
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
        if(scenarios.Visible)using(var pen=new Pen(Color.FromArgb(230,223,237)))e.Graphics.DrawLine(pen,D(36),scenarios.Top-1,D(812),scenarios.Top-1);
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
        float x = D(52) + progress / 100 * D(672);
        int bounce = working ? (int)(Math.Sin(animation * .65) * D(4)) : 0;
        e.Graphics.DrawImageUnscaled(runner, (int)x - D(47), D(20) + bounce);
        using (var track = new SolidBrush(Color.FromArgb(238,235,243))) using(var path=AntShape.Rounded(new RectangleF(D(52),D(107),D(672),D(7)),D(3))) e.Graphics.FillPath(track,path);
        if(progress>0)using(var fill=new SolidBrush(Accent))using(var path=AntShape.Rounded(new RectangleF(D(52),D(107),Math.Max(D(7),D(672)*progress/100),D(7)),D(3)))e.Graphics.FillPath(fill,path);
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
                case 3: Setup.ApplySize(Setup.RequestedPixels); break;
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
            ClientSize = new Size(D(850), D(504)); done.SetBounds(D(518), D(444), D(108), D(40)); done.Visible = true;
        }
    }
    void FinishRestore()
    {
        timer.Stop(); working = false; loading.Visible = false; step.Visible = false; percent.Visible = false;
        var close = Controls.Find("PrototypeClose",true); if (close.Length > 0) close[0].Visible = true;
        var installing = Controls.Find("InstallingStatus",true); if (installing.Length > 0) installing[0].Visible = false;
        title.Text = "原来的鼠标已回来"; subtitle.Text = "已恢复首次安装前的鼠标方案。";
        done.SetBounds(D(518), D(334), D(108), D(40)); done.Visible = true;
    }
    void Finish()
    {
        timer.Stop();working=false;loading.Visible=false;step.Visible=false;percent.Visible=false;
        var close=Controls.Find("PrototypeClose",true);if(close.Length>0)close[0].Visible=true;
        var installing=Controls.Find("InstallingStatus",true);if(installing.Length>0)installing[0].Visible=false;
        title.Text="小猫已就位";subtitle.Text="移动鼠标，试试小猫。";subtitle.Visible=true;subtitle.Font=new Font(Font.FontFamily,10.5f);
        while(scenarios.Controls.Count>0)scenarios.Controls[0].Dispose();BuildScenarios();
        int rows=(scenarios.Controls.Count+1)/2;scenarios.Top=D(180);scenarios.Height=D(rows*78);
        done.Top=back.Top=scenarios.Bottom+D(31);ClientSize=new Size(D(850),done.Bottom+D(22));
        scenarios.Visible=true;done.Visible=true;back.Visible=true;
    }
    internal void Preview(){Finish();}
    internal void VerifySizePreviews()
    {
        foreach(int pixels in new[]{25,45,35})
        {
            Setup.PrepareSize(pixels);Finish();Application.DoEvents();
            foreach(Control card in scenarios.Controls)
            {
                int i=(int)card.Tag;Setup.VerifyTrialCursor(card.Cursor.Handle,Setup.CursorPath(i));
                var image=(CursorPixelPreview)card.Controls[0];
                if(Math.Abs(Math.Max(image.Image.Width,image.Image.Height)-pixels)>3)throw new IOException("尺寸资源与预览不一致");
                image.VerifyCentered();
            }
            using(var bitmap=new Bitmap(Width,Height)){DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));bitmap.Save(Path.Combine(Setup.Root,"preview-"+pixels+".png"));}
        }
        File.WriteAllText(Path.Combine(Setup.Root,"display-check.txt"),"Picker draft sizing, accepted settings, cursor handles and centered trial previews verified. No system pointer settings changed.");
    }
    internal void PreviewLoading() { working = false; progress = 55; step.Text = "备份原鼠标"; percent.Text = "55%"; }
    void BuildScenarios()
    {
        int cardIndex = 0;
        string[] descriptions = { "移动鼠标试试", "输入几个字", "点一下链接", "此操作暂不可用", "查看等待指针", "在区域内移动", "左右移动鼠标", "上下移动鼠标" };
        for (int i = 0; i < 8; i++)
        {
            int index = i;
            if (!Setup.Enabled(i)) continue;
            var card = new ThemeCard { BackColor = Color.FromArgb(255, 255, 255) };
            card.Tag = i;
            card.SetBounds(D(cardIndex % 2 * 400), D(cardIndex++ / 2 * 78), D(376), D(78));
            IntPtr handle = Setup.LoadPreviewCursor(Setup.CursorPath(i));
            if (handle == IntPtr.Zero) throw new IOException("无法加载试用指针");
            var cursor = new Cursor(handle); card.Cursor = cursor;
            card.Disposed += delegate { cursor.Dispose(); Setup.DestroyCursor(handle); };
            Image thumbnail = CursorEncoder.ReadPixels(Setup.CursorPath(i));
            var picture = new CursorPixelPreview { Image = thumbnail, Cursor = cursor };
            picture.SetBounds(D(4), D(15), D(48), D(48)); card.Controls.Add(picture);
            picture.Disposed += delegate { picture.Image.Dispose(); };
            var label = new Label { Text = Setup.Names[i], AutoSize = true, Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold), Cursor = cursor };
            label.SetBounds(D(63), D(13), D(300), D(22)); card.Controls.Add(label);
            var description = new Label { Text = descriptions[i], ForeColor = Muted, Font=new Font(Font.FontFamily,9f), Cursor = cursor };
            description.SetBounds(D(63), D(37), D(300), D(24)); card.Controls.Add(description);
            if (index == 1)
            {
                description.Visible = false;
                var input = new AntInput { Cursor = cursor, Font=new Font(Font.FontFamily,9.75f) };
                input.SetBounds(D(63), D(34), D(200), D(32)); card.Controls.Add(input);
            }
            if (index == 2)
            {
                description.ForeColor = Accent; description.Font = new Font(Font.FontFamily, 9.375f, FontStyle.Underline);
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
    internal BrandHeader() { DoubleBuffered = true; BackColor = Color.White; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.White);
        using (var pen = new Pen(Color.FromArgb(230, 223, 237))) e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

internal sealed class ThemeCard : Panel
{
    internal ThemeCard() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var pen = new Pen(Color.FromArgb(230, 223, 237))) e.Graphics.DrawLine(pen, 0, Height-1, Width, Height-1);
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
        e.Graphics.Clear(hovered?Color.FromArgb(247,244,250):Color.White);float u=Width/376f;
        using(var pen=new Pen(Color.FromArgb(230,223,237)))e.Graphics.DrawLine(pen,0,Height-1,Width,Height-1);
        if(Image!=null)e.Graphics.DrawImageUnscaled(Image,(int)(28*u)-Image.Width/2,(Height-Image.Height)/2);
        TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle((int)(63*u),0,(int)(190*u),Height),Color.FromArgb(41,36,57),TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
        using(var font=new Font(Font.FontFamily,9f))TextRenderer.DrawText(e.Graphics,Status,font,new Rectangle(Width-(int)(90*u),0,(int)(86*u),Height),Color.FromArgb(102,93,116),TextFormatFlags.Right|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-3,-3));
    }
}

internal sealed class AntButton : Button
{
    internal bool Primary, SelectedStyle, CatStyle, Plain, Quiet, Segment, Leading;
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
        float u=CatStyle?Height/56f:Segment?Height/32f:Quiet?Height/34f:Height/42f;
        e.Graphics.Clear(Segment?Color.FromArgb(241,239,244):Parent.BackColor);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Color ink=Color.FromArgb(41,36,57), muted=Color.FromArgb(102,93,116), purple=Color.FromArgb(101,65,138);
        Color fill=Primary?(hovered?Color.FromArgb(86,53,120):purple):Segment?(SelectedStyle?Color.White:hovered?Color.FromArgb(233,229,239):Color.FromArgb(241,239,244)):CatStyle?(SelectedStyle?Color.FromArgb(241,232,250):hovered?Color.FromArgb(245,243,248):Color.White):Quiet?(hovered?Color.FromArgb(233,226,241):Color.FromArgb(242,240,245)):hovered?Color.FromArgb(242,240,245):Color.White;
        if(!Enabled)fill=Primary?Color.FromArgb(190,179,203):Color.FromArgb(246,245,248);
        if(!Plain||hovered)using(var path=AntShape.Rounded(new RectangleF(.5f,.5f,Width-1,Height-1),(CatStyle?14:Segment?7:Quiet?9:10)*u))using(var brush=new SolidBrush(fill))e.Graphics.FillPath(brush,path);
        var text=ClientRectangle;
        if(CatStyle&&Image!=null)
        {
            int size=Image.Width;e.Graphics.DrawImageUnscaled(Image,(int)Math.Round(16*u),(Height-size)/2);text=new Rectangle((int)(58*u),0,Width-(int)(66*u),Height);
            if(SelectedStyle)using(var font=new Font(Font.FontFamily,7.5f))TextRenderer.DrawText(e.Graphics,"✓",font,new Rectangle(Width-(int)(16*u),(int)(3*u),(int)(13*u),(int)(14*u)),purple,TextFormatFlags.NoPadding);
        }
        Color color=Primary?Color.White:!Enabled?Color.FromArgb(128,119,137):SelectedStyle||Quiet?purple:Plain||Segment?muted:ink;
        TextRenderer.DrawText(e.Graphics,Text,Font,text,color,(CatStyle||Leading?TextFormatFlags.Left:TextFormatFlags.HorizontalCenter)|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
        if(Focused&&ShowFocusCues)using(var path=AntShape.Rounded(new RectangleF(2,2,Width-4,Height-4),7*u))using(var pen=new Pen(Primary?Color.White:purple)){pen.DashStyle=System.Drawing.Drawing2D.DashStyle.Dot;e.Graphics.DrawPath(pen,path);}
    }
}

internal sealed class AntInput : UserControl
{
    readonly TextBox editor = new TextBox { BorderStyle = BorderStyle.None, BackColor = Color.White };
    bool hovered;
    const string Hint = "输入几个字";
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
        editor.BackColor=editor.Focused?Color.White:hovered?Color.FromArgb(238,235,242):Color.FromArgb(245,243,247);
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
        using (var path = AntShape.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), 8 * unit))
        using (var brush = new SolidBrush(editor.Focused?Color.White:hovered?Color.FromArgb(238,235,242):Color.FromArgb(245,243,247)))
        using (var pen = new Pen(editor.Focused ? Color.FromArgb(101,65,138) : Color.Transparent, Math.Max(1, unit)))
        { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
    }
}
