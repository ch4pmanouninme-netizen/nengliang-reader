using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
namespace Reader.Core;
    public sealed class Preferences
    {
        public bool HasPosition;
        public int Width = 560, X = -1, Y = -1, Seconds = 6, Opacity = 100, Theme = 4, LineCount = 1;
        public float FontSize = 11;
        public string FontName = "Microsoft YaHei UI";
        public string ChatHost = "", ChatNick = "";
        public string UiTheme = "";
        public string ReadingThemeId = "";
        public int ChatPort = 38417;
        public bool TopMost = true, Stealth = true, ClickThrough, UiDark;
        // 主页窗口关闭时的行为。未选择时，第一次关闭先询问，之后按记住的选择执行。
        public const int ExitUnset = 0, ExitToTray = 1, ExitProgram = 2;
        public int ExitMode = ExitUnset;
        public void UsePreset(int theme)
        {
            ReadingThemeId = ""; Theme = theme; FontSize = theme == 5 ? 10 : 11; Opacity = 100; Stealth = true;
            if (string.IsNullOrEmpty(UiTheme) && (theme == 4 || theme == 5)) UiDark = theme == 5;
        }
    }


