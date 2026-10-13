using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
namespace OneLineReader
{
    internal static class LayoutCheck
    {
        [STAThread] public static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Store store = new Store(args[0]);
            Book book = store.Add("示例.txt", "午后的阳光落在窗边，给桌上的杯子镶了一圈温柔的金边。你把故事打开，整个世界便安静了一点。\n阅读不一定需要一整块时间。有时，一小段空隙，一行文字，就足够让心走到很远的地方。", "示例");
            store.LastId = book.Id;
            using (LibraryForm form = new LibraryForm(store))
            {
                form.Show(); Application.DoEvents();
                Check(form.ClientSize == new Size(620, 360), "document window uses compact 620 by 360 layout");
                Check(form.Text == "文档" && !form.ShowInTaskbar && form.FormBorderStyle == FormBorderStyle.None, "document window has neutral title and no taskbar button");
                Check(AllControls(form).All(c => c.Font.Size <= 11), "main window contains no oversized branding text");
                using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(args[1]); }
                Button appearance = AllControls(form).OfType<Button>().Single(b => b.Text.Contains("企业微信"));
                appearance.PerformClick(); Application.DoEvents();
                Check(form.BackColor == Color.FromArgb(25, 26, 28) && store.Pref.UiDark && store.Pref.Theme == 5, "appearance selector applies IDEA palette to entire main window");
                using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(Path.GetDirectoryName(args[1]), "library-dark-preview.png")); }
                appearance.PerformClick();
                Check(form.BackColor == Color.FromArgb(245, 247, 250) && !store.Pref.UiDark && store.Pref.Theme == 4, "appearance selector restores WeCom palette to entire main window");
                QuietTrackBar progress = AllControls(form).OfType<QuietTrackBar>().Single();
                typeof(QuietTrackBar).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(progress, new object[] { new KeyEventArgs(Keys.PageDown) });
                typeof(Control).GetMethod("OnKeyUp", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(progress, new object[] { new KeyEventArgs(Keys.PageDown) });
                Check(book.Position > 0, "quiet progress control supports keyboard seeking");
                typeof(QuietTrackBar).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(progress, new object[] { new KeyEventArgs(Keys.Home) });
                typeof(Control).GetMethod("OnKeyUp", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(progress, new object[] { new KeyEventArgs(Keys.Home) });
                Check(book.Position == 0, "quiet progress control can return to document start");
                Button start = form.Controls.OfType<Button>().First(b => b.Text == "显示");
                start.PerformClick(); Application.DoEvents();
                ReaderForm reader = Application.OpenForms.OfType<ReaderForm>().Single();
                Check(reader.Visible && !form.Visible, "start button switches from shelf to floating reader");
                int initialHeight = reader.Height;
                Check(initialHeight <= 32 && store.Pref.Stealth && !reader.ShowInTaskbar, "compact text bar is default and has no taskbar button");
                using (Bitmap image = new Bitmap(reader.Width, reader.Height)) { reader.DrawToBitmap(image, new Rectangle(Point.Empty, reader.Size)); image.Save(args[2]); }
                reader.MoveLine(true); Check(book.Position > 0, "reader advances displayed content");
                reader.MoveLine(false); Check(book.Position == 0, "reader restores previous displayed content");
                using (SettingsForm settings = new SettingsForm(store.Pref))
                {
                    settings.Show(); Application.DoEvents();
                    NumericUpDown rows = AllControls(settings).OfType<NumericUpDown>().Single(c => c.Name == "LineCount");
                    Check(rows.Minimum == 1 && rows.Maximum == 10 && rows.Value == 1, "settings exposes one to ten display lines with one-line default");
                    rows.Value = 2;
                    using (Bitmap image = new Bitmap(settings.Width, settings.Height)) { settings.DrawToBitmap(image, new Rectangle(Point.Empty, settings.Size)); image.Save(Path.Combine(Path.GetDirectoryName(args[2]), "settings-preview.png")); }
                    AllControls(settings).OfType<Button>().Single(b => b.Text == "保存设置").PerformClick();
                    Check(store.Pref.LineCount == 2, "saving settings applies chosen line count");
                }
                reader.ApplyPreferences();
                ReadingPage twoRows = (ReadingPage)typeof(ReaderForm).GetField("page", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(reader);
                Check(twoRows.Lines.Count == 2 && reader.Height == (initialHeight - 6) * 2 + 6 && book.Position == 0, "two-line reader grows to fit text without changing reading position");
                using (Bitmap image = new Bitmap(reader.Width, reader.Height)) { reader.DrawToBitmap(image, new Rectangle(Point.Empty, reader.Size)); image.Save(Path.Combine(Path.GetDirectoryName(args[2]), "two-line-preview.png")); }
                int twoEnd = twoRows.End; reader.MoveLine(true);
                Check(book.Position >= twoEnd, "next page advances past both visible rows");
                reader.MoveLine(false); Check(book.Position == 0, "previous page restores the whole two-line page");
                store.Pref.LineCount = 10; store.Pref.FontSize = 32; reader.ApplyPreferences();
                Check(reader.Height > 100 && reader.Bottom <= Screen.FromControl(reader).WorkingArea.Bottom, "ten large-font lines are not capped at old single-line height or below screen");
                using (SettingsForm cancelSettings = new SettingsForm(store.Pref))
                {
                    cancelSettings.Show(); Application.DoEvents();
                    AllControls(cancelSettings).OfType<NumericUpDown>().Single(c => c.Name == "LineCount").Value = 4;
                    AllControls(cancelSettings).OfType<Button>().Single(b => b.Text == "取消").PerformClick();
                    Check(store.Pref.LineCount == 10, "cancel leaves line count unchanged");
                }
                store.Pref.LineCount = 1; store.Pref.FontSize = 11; reader.ApplyPreferences();
                store.Pref.Theme = 1; store.Pref.Opacity = 60; store.Pref.FontSize = 18; store.Pref.Seconds = 2; store.Pref.LineCount = 2; reader.ApplyPreferences();
                Check(reader.BackColor.R == 32 && Math.Abs(reader.Opacity - 0.6) < 0.01 && reader.Height > initialHeight, "theme, opacity and font settings apply to reader");
                reader.ToggleAuto();
                Stopwatch wait = Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < 2200) { Application.DoEvents(); Thread.Sleep(10); }
                Check(book.Position > 0, "automatic reading timer advances multiline content");
                typeof(ReaderForm).GetMethod("HideReader", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(reader, null);
                System.Windows.Forms.Timer auto = (System.Windows.Forms.Timer)typeof(ReaderForm).GetField("auto", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(reader);
                Check(!reader.Visible && !auto.Enabled, "hiding reader pauses automatic reading");
                Store restored = new Store(args[0]);
                Check(restored.Books[0].Position == book.Position && restored.Pref.LineCount == 2, "hiding reader immediately persists reading position and line count");
                form.UsePreset(5); Check(store.Pref.Theme == 5 && reader.BackColor == Color.FromArgb(25, 26, 28) && store.Pref.FontSize == 10, "IDEA preset applies muted dark palette and small font");
                using (Bitmap image = new Bitmap(reader.Width, reader.Height)) { reader.DrawToBitmap(image, new Rectangle(Point.Empty, reader.Size)); image.Save(Path.Combine(Path.GetDirectoryName(args[2]), "idea-preview.png")); }
                store.Pref.Theme = 6; reader.ApplyPreferences(); Check(reader.TransparencyKey == Color.Fuchsia, "text-only theme enables transparent background");
                form.UsePreset(4); Check(reader.TransparencyKey == Color.Empty && reader.BackColor == Color.FromArgb(245, 247, 250), "light preset clears transparent background");
                form.ToggleClickThrough();
                int style = Native.GetWindowLong(reader.Handle, -20);
                Check(reader.ClickThrough && (style & 0x20) != 0 && (style & 0x80000) != 0 && (style & 0x08000000) != 0, "click-through applies layered, pass-through and no-activate styles");
                form.ToggleClickThrough(); Check(!reader.ClickThrough && (Native.GetWindowLong(reader.Handle, -20) & (0x20 | 0x08000000)) == 0, "tray action can restore interactive reader styles");
                store.Pref.ClickThrough = true;
                ReadingSession startupSession = new ReadingSession(book, store.TextFor(book));
                using (ReaderForm resumedPass = new ReaderForm(form, store, startupSession))
                {
                    resumedPass.Show(); Application.DoEvents();
                    uint colorKey, flags; byte alpha;
                    Check(resumedPass.ClickThrough && GetLayeredWindowAttributes(resumedPass.Handle, out colorKey, out alpha, out flags) && alpha == 255, "persisted click-through starts with a visible fully initialized layered window");
                }
                store.Pref.ClickThrough = false;
                form.ShowLibrary(); Check(form.Visible && !reader.Visible, "returning to shelf displays shelf only");
                start.PerformClick(); Application.DoEvents(); Check(reader.Visible && !form.Visible, "resume reuses current reading session");
                form.ExitApp(); Check(form.IsDisposed && reader.IsDisposed, "exit disposes shelf and floating reader");
            }
            using (LibraryForm empty = new LibraryForm(new Store(Path.Combine(args[0], "empty-state"))))
            {
                empty.Show(); Application.DoEvents();
                string[] banned = { "慢慢读", "欢迎使用", "故事很长", "给故事", "能良研发" };
                Check(!AllControls(empty).Any(c => banned.Any(s => c.Text.Contains(s))), "empty main window has no branding or promotional copy");
                empty.ExitApp();
            }
            Console.WriteLine("SUCCESS: 32 application integration checks passed.");
            return 0;
        }
        private static void Check(bool success, string label) { if (!success) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
        private static System.Collections.Generic.IEnumerable<Control> AllControls(Control parent)
        {
            foreach (Control control in parent.Controls) { yield return control; foreach (Control nested in AllControls(control)) yield return nested; }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint colorKey, out byte alpha, out uint flags);
    }
}

