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
    /// <summary>
    /// 界面与阅读偏好，持久化在 state.xml 的 preferences 节点中。
    /// 加载时会按各自的合法范围夹取数值，防止手工修改或旧版本数据造成异常值。
    /// </summary>
    public sealed class Preferences
    {
        /// <summary>是否恢复上次保存的窗口位置；旧版本没有该属性时由 X/Y 推断。</summary>
        public bool HasPosition;
        /// <summary>窗口宽度、窗口坐标、自动翻页秒数、不透明度百分比、阅读主题编号、每页行数。</summary>
        /// <remarks>坐标 -1 表示未定位；不透明度合法范围为 30–100。</remarks>
        public int Width = 560, X = -1, Y = -1, Seconds = 6, Opacity = 100, Theme = 4, LineCount = 1;
        /// <summary>正文字号（磅）。</summary>
        public float FontSize = 11;
        /// <summary>正文字体名称。</summary>
        public string FontName = "Microsoft YaHei UI";
        /// <summary>聊天服务器地址与昵称；空字符串表示未配置。</summary>
        public string ChatHost = "", ChatNick = "";
        /// <summary>界面主题标识；空字符串表示使用内置默认主题。</summary>
        public string UiTheme = "";
        /// <summary>阅读主题标识；空字符串表示使用预设 <see cref="Theme"/>。</summary>
        public string ReadingThemeId = "";
        /// <summary>聊天服务器端口，合法范围 1–65535。</summary>
        public int ChatPort = 38417;
        /// <summary>窗口置顶、极简阅读条（隐藏底部提示）、鼠标穿透与界面深色模式开关。</summary>
        public bool TopMost = true, Stealth = true, ClickThrough, UiDark;
        // 主页窗口关闭时的行为。未选择时，第一次关闭先询问，之后按记住的选择执行。
        /// <summary>主窗口关闭行为的取值：<see cref="ExitUnset"/> 未选择，<see cref="ExitToTray"/> 最小化到托盘，<see cref="ExitProgram"/> 退出程序。</summary>
        public const int ExitUnset = 0, ExitToTray = 1, ExitProgram = 2;
        /// <summary>当前关闭行为，取值见上方常量。</summary>
        public int ExitMode = ExitUnset;
        /// <summary>
        /// 应用内置阅读预设：切换主题编号，并重置字号、不透明度与隐身状态。
        /// </summary>
        /// <param name="theme">预设主题编号；编号 5 为深色预设，字号略小。</param>
        /// <remarks>仅在用户尚未自定义界面主题时，才根据深色预设同步界面明暗。</remarks>
        public void UsePreset(int theme)
        {
            ReadingThemeId = ""; Theme = theme; FontSize = theme == 5 ? 10 : 11; Opacity = 100; Stealth = true;
            if (string.IsNullOrEmpty(UiTheme) && (theme == 4 || theme == 5)) UiDark = theme == 5;
        }
    }


