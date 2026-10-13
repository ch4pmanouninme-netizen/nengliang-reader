using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

namespace OneLineReader
{
    internal static class Ui
    {
        public static readonly Color Ink = Color.FromArgb(37, 47, 43), Muted = Color.FromArgb(117, 126, 119), Green = Color.FromArgb(41, 91, 70), Paper = Color.FromArgb(247, 248, 244), Line = Color.FromArgb(224, 229, 221);
        public static Font Font(float size, FontStyle style = FontStyle.Regular) { return new Font("Microsoft YaHei UI", size, style); }
        public static Label Label(string text, int x, int y, int w, int h, float size, Color color, FontStyle style = FontStyle.Regular)
        {
            return new Label { Text = text, Bounds = new Rectangle(x, y, w, h), Font = Font(size, style), ForeColor = color, BackColor = Color.Transparent, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        }
        public static Button Button(string text, int x, int y, int w, int h, bool primary)
        {
            Button b = new Button { Text = text, Bounds = new Rectangle(x, y, w, h), FlatStyle = FlatStyle.Flat, Font = Font(10), BackColor = primary ? Green : Color.White, ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderColor = primary ? Green : Line; b.FlatAppearance.BorderSize = primary ? 0 : 1;
            return b;
        }
        public static Color Surface(bool dark) { return dark ? Color.FromArgb(25, 26, 28) : Color.FromArgb(245, 247, 250); }
        public static Color TextColor(bool dark) { return dark ? Color.FromArgb(182, 186, 194) : Color.FromArgb(64, 69, 76); }
        public static Color MutedColor(bool dark) { return dark ? Color.FromArgb(127, 133, 141) : Color.FromArgb(138, 144, 153); }
        public static void Skin(Control root, bool dark)
        {
            root.ForeColor = TextColor(dark);
            root.BackColor = root.Name == "header" ? (dark ? Color.FromArgb(37, 40, 44) : Color.FromArgb(237, 240, 244))
                : root.Name == "side" ? (dark ? Color.FromArgb(32, 33, 36) : Color.FromArgb(235, 238, 242))
                : root.Name == "preview" ? (dark ? Surface(true) : Color.White) : root.Parent == null ? Surface(dark) : root.Parent.BackColor;
            Label label = root as Label;
            if (label != null) { root.BackColor = Color.Transparent; if (root.Font.Size <= 9) root.ForeColor = MutedColor(dark); }
            Button button = root as Button;
            if (button != null)
            {
                button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false;
                button.BackColor = dark ? Color.FromArgb(43, 45, 48) : Color.FromArgb(248, 249, 251);
                button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.BorderColor = dark ? Color.FromArgb(54, 57, 61) : Color.FromArgb(217, 222, 228);
                button.FlatAppearance.MouseOverBackColor = dark ? Color.FromArgb(54, 57, 61) : Color.FromArgb(231, 235, 241);
            }
            ComboBox combo = root as ComboBox;
            if (combo != null)
            {
                combo.FlatStyle = FlatStyle.Flat; combo.BackColor = dark ? Color.FromArgb(43, 45, 48) : Color.FromArgb(248, 249, 251);
                if (combo.DropDownStyle == ComboBoxStyle.DropDownList)
                {
                    combo.DrawMode = DrawMode.OwnerDrawFixed; combo.DrawItem -= DrawChoice; combo.DrawItem += DrawChoice;
                }
            }
            if (root is TextBoxBase || root is NumericUpDown) root.BackColor = dark ? Color.FromArgb(43, 45, 48) : Color.White;
            Form form = root as Form;
            if (form != null && form.IsHandleCreated) ThemeNative.Caption(form.Handle, dark);
            foreach (Control child in root.Controls) Skin(child, dark);
        }
        private static void DrawChoice(object sender, DrawItemEventArgs e)
        {
            ComboBox combo = (ComboBox)sender;
            using (Brush background = new SolidBrush(combo.BackColor)) e.Graphics.FillRectangle(background, e.Bounds);
            if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, combo.Items[e.Index].ToString(), combo.Font, e.Bounds, combo.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }
        public static void QuietHeader(Form form, string title, Action hide, Action close)
        {
            foreach (Control child in form.Controls) child.Top += 34;
            form.ClientSize = new Size(form.ClientSize.Width, form.ClientSize.Height + 34); form.FormBorderStyle = FormBorderStyle.None;
            Panel header = new Panel { Name = "header", Bounds = new Rectangle(0, 0, form.ClientSize.Width, 34) }; form.Controls.Add(header);
            Label caption = Label(title, 12, 4, form.ClientSize.Width - 96, 26, 9, Ink); header.Controls.Add(caption);
            MouseEventHandler drag = (s, e) => { if (e.Button == MouseButtons.Left) { ThemeNative.Drag(form.Handle); } }; header.MouseDown += drag; caption.MouseDown += drag;
            Button minimize = Button("—", form.ClientSize.Width - 66, 4, 26, 25, false); minimize.Click += (s, e) => hide(); header.Controls.Add(minimize);
            Button exit = Button("×", form.ClientSize.Width - 34, 4, 26, 25, false); exit.Click += (s, e) => close(); header.Controls.Add(exit);
        }
    }


    internal static class SavedAppearance
    {
        public static string DefaultPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NengliangReader", "state.xml"); } }
        public static bool Read(string path, bool fallback)
        {
            try
            {
                if (!File.Exists(path)) return fallback;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (XmlReader xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 }))
                {
                    XElement root = XElement.Load(xml), pref = root.Name == "reader" ? root.Element("preferences") : null;
                    if (pref == null) return fallback;
                    string dark = (string)pref.Attribute("uiDark");
                    return dark == null ? (string)pref.Attribute("theme") == "1" || (string)pref.Attribute("theme") == "5" : dark == "true";
                }
            }
            catch (Exception ex) { if (!(ex is IOException || ex is UnauthorizedAccessException || ex is XmlException)) throw; return fallback; }
        }
    }
    internal static class ThemeNative
    {
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        public static void Drag(IntPtr handle) { ReleaseCapture(); SendMessage(handle, 0xA1, new IntPtr(2), IntPtr.Zero); }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        public static void Caption(IntPtr handle, bool dark)
        {
            int value = dark ? 1 : 0;
            try { if (DwmSetWindowAttribute(handle, 20, ref value, 4) != 0) DwmSetWindowAttribute(handle, 19, ref value, 4); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
    }
}

