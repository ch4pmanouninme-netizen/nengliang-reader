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
    /// 一页的排版结果：按段落拆好的各行文本，以及本页结束后的下一个字符位置。
    /// </summary>
    public sealed class ReadingPage
    {
        /// <summary>本页各行文本，已按度量函数切分。</summary>
        public readonly List<string> Lines = new List<string>();
        /// <summary>下一页的起始位置（正文中的 UTF-16 下标）。</summary>
        public int End;
    }


