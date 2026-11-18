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
        if (args.Length == 2 && args[0] == "--integration-test")
        {
            Setup.Root = Path.GetFullPath(args[1]);
            Setup.Prepare(); Setup.CopyCursors(); Setup.SaveBackup();
            try { Setup.Apply(); }
            finally { Setup.Restore(); }
            Setup.VerifyRestored();
            File.WriteAllText(Path.Combine(Setup.Root, "integration-passed.txt"), "Applied all eight cursors, refreshed Windows, restored original values, refreshed Windows again, verified registry values and types match backup.");
            return;
        }
        if (args.Length == 2 && args[0] == "--self-test")
        {
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
            Application.Run(new InstallerWindow((args.Length > 0 && args[0] == "--restore") || Path.GetFileName(Application.ExecutablePath).StartsWith("恢复")));
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
    internal static bool Changed;
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadCursorFromFile(string path);
    [DllImport("user32.dll")] internal static extern bool DestroyCursor(IntPtr cursor);

    internal static Stream Resource(string name) { return Assembly.GetExecutingAssembly().GetManifestResourceStream(name); }
    internal static Image Picture(int index)
    {
        using (var stream = Resource("picture" + index))
        using (var image = Image.FromStream(stream)) return new Bitmap(image);
    }
    internal static void Prepare()
    {
        Directory.CreateDirectory(Root);
        // Validate every bundled cursor before modifying the user's settings.
        for (int i = 0; i < Names.Length; i++)
        {
            string path = Path.Combine(Root, "check-" + i + ".cur");
            using (var source = Resource("cursor" + i))
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
        if (File.Exists(Backup)) { XDocument.Load(Backup); return; }
        var root = new XElement("cursorBackup");
        foreach (string role in Roles) root.Add(Capture(CursorKey, role));
        root.Add(Capture(CursorKey, ""));
        root.Add(Capture(CursorKey, "Scheme Source"));
        root.Add(Capture(CursorKey + @"\Schemes", "喵喵就位"));
        string temp = Backup + ".tmp";
        new XDocument(root).Save(temp);
        File.Move(temp, Backup);
    }
    internal static void CopyCursors()
    {
        // Stable content-specific directory keeps files used by an older install intact.
        string folder = Path.Combine(Root, "cursors-v1");
        Directory.CreateDirectory(folder);
        for (int i = 0; i < Names.Length; i++)
        {
            string target = Path.Combine(folder, Names[i] + ".cur");
            if (File.Exists(target)) continue;
            string temp = target + ".tmp";
            using (var source = Resource("cursor" + i))
            using (var destination = File.Create(temp)) source.CopyTo(destination);
            File.Move(temp, target);
        }
    }
    internal static void Apply()
    {
        Changed = true;
        using (var key = Registry.CurrentUser.CreateSubKey(CursorKey))
        {
            for (int i = 0; i < Roles.Length; i++)
            {
                string path = Path.Combine(Root, "cursors-v1", Names[i] + ".cur");
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
        }
        Reload();
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
        foreach (var expected in XDocument.Load(Backup).Root.Elements("value"))
        {
            var actual = Capture((string)expected.Attribute("path"), (string)expected.Attribute("name"));
            if (!XNode.DeepEquals(expected, actual)) throw new IOException("恢复验证失败：" + (string)expected.Attribute("name"));
        }
    }
    internal static void Restore()
    {
        if (!File.Exists(Backup)) throw new IOException("还没有安装备份，无需恢复。");
        var document = XDocument.Load(Backup);
        foreach (var element in document.Root.Elements("value"))
        {
            string path = (string)element.Attribute("path");
            if (path != CursorKey && path != CursorKey + @"\Schemes") throw new IOException("备份格式不正确。");
            string name = (string)element.Attribute("name");
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
        Reload();
        Changed = false;
    }
}

internal sealed class InstallerWindow : Form
{
    readonly Color Ink = Color.FromArgb(55, 46, 37), Muted = Color.FromArgb(133, 114, 93), Accent = Color.FromArgb(183, 139, 88);
    readonly Label title = new Label(), subtitle = new Label(), step = new Label(), percent = new Label();
    readonly Panel loading = new SmoothPanel(), scenarios = new Panel();
    readonly Button done = new AntButton { Primary = true }, restore = new AntButton();
    readonly Button closeWindow = new AntButton();
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
        Font = new Font("Microsoft YaHei", 10);
        BackColor = Color.FromArgb(255, 250, 244); ForeColor = Ink;
        ClientSize = new Size(660, 424); FormBorderStyle = FormBorderStyle.None;
        AutoScroll = true;
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
        Font = new Font("Microsoft YaHei", 10 * displayScale);
        title.Font = new Font(Font.FontFamily, 19 * displayScale, FontStyle.Bold);
        brand.Font = new Font(Font.FontFamily, 21 * displayScale, FontStyle.Bold);
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
        using (var pen = new Pen(Color.FromArgb(230, 208, 183))) e.Graphics.DrawLine(pen, D(30), done.Top - D(18), ClientSize.Width - D(30), done.Top - D(18));
    }
    static Image ResizePicture(Image original, int size, bool trimTransparent = false)
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
        ClientSize = new Size(D(660), D(690));
        title.Text = "已完成，请试用鼠标效果";
        subtitle.Text = "把鼠标移到下面的区域，看看两只小猫的表现。";
        BuildScenarios(); scenarios.Visible = true; done.Visible = true; restore.Visible = true;
    }
    internal void Preview() { Finish(); }
    internal void PreviewLoading() { working = false; progress = 55; step.Text = "安放两只小猫"; percent.Text = "55%"; }
    void BuildScenarios()
    {
        string[] descriptions = { "在空白区域移动鼠标", "在这里输入文字", "试试这个链接", "此操作暂不可用", "等待任务处理完成", "按住拖动这只小猫", "左右移动鼠标试用", "上下移动鼠标试用" };
        for (int i = 0; i < 8; i++)
        {
            int index = i;
            var card = new ThemeCard { BackColor = Color.FromArgb(252, 246, 237) };
            card.SetBounds(D(i % 2 * 306), D(i / 2 * 100), D(294), D(90));
            IntPtr handle = Setup.LoadCursorFromFile(Path.Combine(Setup.Root, "cursors-v1", Setup.Names[i] + ".cur"));
            var cursor = new Cursor(handle); card.Cursor = cursor;
            card.Disposed += delegate { cursor.Dispose(); Setup.DestroyCursor(handle); };
            Image thumbnail;
            using (var original = Setup.Picture(i)) thumbnail = ResizePicture(original, D(64), true);
            var picture = new PictureBox { Image = thumbnail, SizeMode = PictureBoxSizeMode.CenterImage, Cursor = cursor };
            picture.SetBounds(D(10), D(6), D(82), D(82)); card.Controls.Add(picture);
            picture.Disposed += delegate { picture.Image.Dispose(); };
            var label = new Label { Text = Setup.Names[i], AutoSize = true, Font = new Font(Font, FontStyle.Bold), Cursor = cursor };
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
        using (var pen = new Pen(Color.FromArgb(232, 210, 185))) e.Graphics.DrawPath(pen, path);
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
internal sealed class AntButton : Button
{
    internal bool Primary;
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
        Color blue = Color.FromArgb(174, 105, 49), hover = Color.FromArgb(192, 125, 66), active = Color.FromArgb(143, 80, 34);
        Color state = pressed ? active : hovered ? hover : blue;
        var rectangle = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var shape = AntShape.Rounded(rectangle, 6 * unit))
        using (Brush brush = Primary ? (Brush)new System.Drawing.Drawing2D.LinearGradientBrush(rectangle, Color.FromArgb(Math.Min(255, state.R + 16), Math.Min(255, state.G + 14), Math.Min(255, state.B + 12)), state, 90f) : new SolidBrush(Color.FromArgb(255, 252, 246)))
        using (var pen = new Pen(Primary || hovered || pressed ? state : Color.FromArgb(219, 193, 166), Math.Max(1, unit)))
        {
            e.Graphics.FillPath(brush, shape); e.Graphics.DrawPath(pen, shape);
        }
        if (Focused && ShowFocusCues)
        {
            using (var shape = AntShape.Rounded(new RectangleF(3, 3, Width - 7, Height - 7), 4 * unit))
            using (var pen = new Pen(Primary ? Color.White : blue)) { pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot; e.Graphics.DrawPath(pen, shape); }
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Primary ? Color.White : hovered ? hover : Color.FromArgb(98, 65, 40), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
