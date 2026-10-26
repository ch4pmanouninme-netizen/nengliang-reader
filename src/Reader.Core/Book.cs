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
    /// 书库中一本书的元数据（持久化到 state.xml）。正文按 <see cref="Id"/> 单独存放在
    /// books/&lt;id&gt;.txt，不在此对象内。
    /// </summary>
    public sealed class Book
    {
        /// <summary>书籍 ID（正文的 SHA-256 十六进制）、显示标题、来源格式（"TXT" 或 "EPUB"）。</summary>
        public string Id, Title, Format;
        /// <summary>阅读位置、书签位置、正文长度，单位均为 UTF-16 字符下标；书签为 -1 表示未设置。</summary>
        public int Position, Bookmark = -1, Length;
        /// <summary>是否已读完；为 true 时进度显示 100%。</summary>
        public bool Completed;
        /// <summary>是否置顶；置顶书籍排在书库列表前面。</summary>
        public bool IsPinned;
        /// <summary>所属分组名；空字符串表示未分组。</summary>
        public string Group = "";
        /// <summary>返回书名，便于界面直接绑定显示。</summary>
        public override string ToString() { return Title; }
    }


