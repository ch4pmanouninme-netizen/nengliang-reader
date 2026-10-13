using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace OneLineReader
{
    internal interface IWindowModule : IDisposable
    {
        string Id { get; }
        string Title { get; }
        string Description { get; }
        Form Window { get; }
        void Open();
    }

    internal sealed class WindowModule : IWindowModule
    {
        private readonly Func<Form> create;
        private readonly Action<Form> open;
        public string Id { get; private set; }
        public string Title { get; private set; }
        public string Description { get; private set; }
        public Form Window { get; private set; }
        public WindowModule(string id, string title, string description, Func<Form> create, Action<Form> open = null)
        {
            Id = id; Title = title; Description = description; this.create = create; this.open = open;
        }
        public void Open()
        {
            if (Window == null || Window.IsDisposed) Window = create();
            if (open != null) open(Window); else { Window.Show(); Window.Activate(); }
        }
        public void Dispose()
        {
            LibraryForm reading = Window as LibraryForm;
            if (reading != null && !reading.IsDisposed) reading.Shutdown();
            if (Window != null) Window.Dispose();
        }
    }

    internal sealed class AppController : ApplicationContext
    {
        private readonly Store store;
        private readonly List<IWindowModule> modules = new List<IWindowModule>();
        private readonly NotifyIcon tray;
        private readonly List<Form> hidden = new List<Form>();
        private bool exiting;
        public HomeForm Home { get; private set; }
        public IEnumerable<IWindowModule> Modules { get { return modules.AsReadOnly(); } }
        public bool Dark { get { return store.Pref.UiDark; } }
        public AppController(Store store)
        {
            this.store = store;
            modules.Add(new WindowModule("reading", "阅读", "TXT / EPUB · 悬浮阅读", () => new LibraryForm(store, this), f => ((LibraryForm)f).ShowLibrary()));
            modules.Add(new WindowModule("chat", "聊天", "局域网 · 加密文字会话", () => new ChatForm(store, SaveNow, ShowHome)));
            Home = new HomeForm(this); MainForm = Home;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("主页", null, (s, e) => ShowHome());
            menu.Items.Add("企业微信浅色", null, (s, e) => UsePreset(4));
            menu.Items.Add("IDEA 深色", null, (s, e) => UsePreset(5));
            menu.Items.Add("退出良子小窗", null, (s, e) => ExitApplication());
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "良子小窗", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (s, e) => ShowHome();
            Home.Show();
        }
        public void OpenModule(string id)
        {
            IWindowModule module = modules.FirstOrDefault(m => m.Id == id);
            if (module == null) throw new ArgumentException("未找到功能模块。", "id");
            module.Open();
        }
        public void ShowHome() { if (!exiting) { Home.Show(); Home.Activate(); } }
        public void SaveNow()
        {
            try { store.Save(); }
            catch (System.IO.IOException) { MessageBox.Show(Home, "设置保存失败，请检查本机磁盘和应用数据目录。", "良子小窗"); }
            catch (UnauthorizedAccessException) { MessageBox.Show(Home, "设置保存失败，应用数据目录不可写。", "良子小窗"); }
        }
        public void UsePreset(int theme)
        {
            LibraryForm reading = modules.Select(m => m.Window).OfType<LibraryForm>().FirstOrDefault(f => !f.IsDisposed);
            if (reading != null) reading.UsePreset(theme);
            else { store.Pref.UsePreset(theme); SaveNow(); ApplyTheme(); }
        }
        public void ApplyTheme()
        {
            Home.ApplyTheme();
            foreach (ChatForm chat in modules.Select(m => m.Window).OfType<ChatForm>().Where(f => !f.IsDisposed)) chat.ApplyTheme();
        }
        public bool HideVisibleChat()
        {
            if (!modules.Select(m => m.Window).OfType<ChatForm>().Any(f => !f.IsDisposed && f.Visible)) return false;
            ToggleVisibility(); return true;
        }
        public void ToggleVisibility()
        {
            Form[] visible = Application.OpenForms.Cast<Form>().Where(f => f.Visible && !f.Modal
                && (f == Home || modules.Any(m => m.Window == f) || f is ReaderForm)).ToArray();
            if (visible.Length > 0)
            {
                hidden.Clear(); hidden.AddRange(visible);
                foreach (Form form in visible) { ReaderForm reader = form as ReaderForm; if (reader != null) reader.Pause(); form.Hide(); }
            }
            else
            {
                foreach (Form form in hidden.Where(f => !f.IsDisposed)) form.Show();
                if (!hidden.Any(f => !f.IsDisposed)) ShowHome(); hidden.Clear();
            }
        }
        public void ExitApplication()
        {
            if (exiting) return; exiting = true;
            foreach (IWindowModule module in modules) module.Dispose();
            SaveNow(); tray.Visible = false; tray.Dispose(); Home.AllowExit = true; Home.Close(); ExitThread();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !exiting) ExitApplication();
            base.Dispose(disposing);
        }
    }

    internal sealed class HomeForm : Form
    {
        private readonly AppController app;
        private readonly Button theme;
        private bool hideRegistered;
        internal bool AllowExit;
        public HomeForm(AppController app)
        {
            this.app = app;
            Text = "良子小窗"; ClientSize = new Size(408, 268); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.None; Font = Ui.Font(9);
            Controls.Add(Ui.Label("良子小窗", 20, 16, 180, 28, 11, Ui.Ink));
            theme = Ui.Button("", 228, 18, 160, 26, false); theme.Name = "Appearance"; theme.Click += (s, e) => app.UsePreset(app.Dark ? 4 : 5); Controls.Add(theme);
            FlowLayoutPanel entries = new FlowLayoutPanel { Bounds = new Rectangle(20, 62, 368, 144), FlowDirection = FlowDirection.TopDown, WrapContents = false };
            Controls.Add(entries);
            foreach (IWindowModule module in app.Modules)
            {
                IWindowModule entry = module;
                Button button = Ui.Button(entry.Title + "   ·   " + entry.Description, 0, 0, 362, 56, false);
                button.Name = "Module-" + entry.Id; button.TextAlign = ContentAlignment.MiddleLeft; button.Padding = new Padding(10, 0, 0, 0);
                button.Click += (s, e) => app.OpenModule(entry.Id); entries.Controls.Add(button);
            }
            Controls.Add(Ui.Label("功能独立运行 · 关闭一个不影响另一个", 20, 212, 368, 20, 8, Ui.Muted));
            Controls.Add(Ui.Label("关闭入口会收进托盘 · 托盘菜单可退出程序", 20, 237, 368, 20, 8, Ui.Muted));
            Ui.QuietHeader(this, Text, Hide, Close); ApplyTheme();
        }
        public void ApplyTheme() { Ui.Skin(this, app.Dark); theme.Text = app.Dark ? "IDEA · 深色 ⇄" : "企业微信 · 浅色 ⇄"; }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e); hideRegistered = Native.RegisterHotKey(Handle, 80, 0x4003, (uint)Keys.H); ThemeNative.Caption(Handle, app.Dark);
        }
        protected override void OnHandleDestroyed(EventArgs e) { if (hideRegistered) Native.UnregisterHotKey(Handle, 80); base.OnHandleDestroyed(e); }
        protected override void WndProc(ref Message m) { if (m.Msg == 0x0312 && m.WParam.ToInt32() == 80) app.ToggleVisibility(); base.WndProc(ref m); }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys key) { if (key == Keys.Escape) { Hide(); return true; } return base.ProcessCmdKey(ref msg, key); }
    }
}
