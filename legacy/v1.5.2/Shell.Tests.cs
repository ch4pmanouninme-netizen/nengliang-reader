using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace OneLineReader
{
    internal static class ShellTests
    {
        private static int checks;
        private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
        private static IEnumerable<Control> All(Control root)
        {
            foreach (Control child in root.Controls) { yield return child; foreach (Control nested in All(child)) yield return nested; }
        }
        private static void Click(Form form, string name) { All(form).OfType<Button>().Single(b => b.Name == name).PerformClick(); Application.DoEvents(); }
        [STAThread] public static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Store store = new Store(Path.Combine(args[0], "shell-data"));
                store.Add("独立窗口测试", "第一章\n用于测试主菜单不受子模块开关影响。", "TXT"); store.Save();
                using (AppController app = new AppController(store))
                {
                    int exits = 0; app.ThreadExit += (s, e) => exits++;
                    HomeForm home = app.Home; Application.DoEvents();
                    Check(home.Visible && app.MainForm == home, "home owns application lifetime");
                    Click(home, "Module-reading");
                    LibraryForm reading = (LibraryForm)app.Modules.Single(m => m.Id == "reading").Window;
                    Check(home.Visible && reading.Visible && app.MainForm == home, "opening reading preserves visible home");
                    home.Hide(); Click(reading, "Home");
                    Check(home.Visible && reading.Visible && app.Modules.Single(m => m.Id == "chat").Window == null, "reading home button returns to menu without opening chat");
                    Check(All(reading).OfType<Button>().All(b => b.Text != "聊天"), "reading has no direct chat navigation button");
                    Click(home, "Module-chat");
                    ChatForm chat = (ChatForm)app.Modules.Single(m => m.Id == "chat").Window;
                    Check(home.Visible && reading.Visible && chat.Visible, "opening chat preserves home and reading");
                    TextBox draft = All(chat).OfType<TextBox>().Single(t => t.Name == "Compose"); draft.Text = "保留未发送草稿";
                    home.Hide(); Click(chat, "Home");
                    Check(home.Visible && chat.Visible && reading.Visible && draft.Text == "保留未发送草稿", "chat home button preserves chat window and draft");
                    Click(chat, "CloseChat");
                    Check(chat.IsDisposed && home.Visible && !home.IsDisposed && reading.Visible && exits == 0, "chat close button cannot hide or close home");
                    Click(home, "Module-chat"); chat = (ChatForm)app.Modules.Single(m => m.Id == "chat").Window;
                    Click(reading, "CloseReading");
                    Check(home.Visible && !reading.Visible && chat.Visible && exits == 0, "reading close button leaves home and chat unchanged");
                    home.Hide(); app.OpenModule("reading"); Application.DoEvents();
                    Check(!home.Visible && reading.Visible, "opening a module does not restore deliberately hidden home");
                    Click(chat, "CloseChat");
                    Check(!home.Visible && !home.IsDisposed && reading.Visible && exits == 0, "closing chat preserves deliberately hidden home");
                    app.OpenModule("chat"); chat = (ChatForm)app.Modules.Single(m => m.Id == "chat").Window;
                    reading.Controls.OfType<Button>().Single(b => b.Text == "显示").PerformClick(); Application.DoEvents();
                    ReaderForm floating = Application.OpenForms.OfType<ReaderForm>().Single();
                    ToolStripItem returnHome = floating.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "返回主菜单");
                    Check(!floating.ContextMenuStrip.Items.Cast<ToolStripItem>().Any(item => item.Text == "打开聊天"), "floating reader menu has no direct chat navigation");
                    int position = store.Books[0].Position; returnHome.PerformClick(); Application.DoEvents();
                    Check(home.Visible && floating.Visible && !floating.IsDisposed && store.Books[0].Position == position && chat.Visible, "floating reader returns to home without changing reading or chat");
                    home.Hide(); reading.CloseReading(); Application.DoEvents();
                    Check(floating.IsDisposed && !home.Visible && chat.Visible && exits == 0, "closing floating reading preserves hidden home and active chat");
                    app.ShowHome(); app.OpenModule("reading"); home.Close(); Application.DoEvents();
                    Check(!home.Visible && !home.IsDisposed && reading.Visible && chat.Visible && exits == 0, "closing home only hides home without closing modules");
                    app.ShowHome(); Click(chat, "CloseChat");
                    Check(home.Visible && app.MainForm == home && exits == 0, "home remains usable after repeated module closes");
                    app.ExitApplication();
                    Check(home.IsDisposed && reading.IsDisposed && exits > 0, "explicit application exit still closes all windows");
                }
                Console.WriteLine("SUCCESS: " + checks + " home lifecycle regression checks passed."); return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex); return 1; }
        }
    }
}

