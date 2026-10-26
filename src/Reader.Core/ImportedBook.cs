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
    /// 导入器解析完成、尚未写入书库的一本书：书名、规范化后的纯文本、来源格式，
    /// 以及可选的章节索引。
    /// </summary>
    public sealed class ImportedBook
    {
        /// <summary>书名、规范化后的正文、来源格式（"TXT" 或 "EPUB"）。</summary>
        public string Title, Text, Format;
        // 导入时生成的章节索引：EPUB 来自书内 nav/NCX 目录，TXT 来自标题启发式识别。
        // 随书保存为 books/<id>.chapters.tsv；阅读端在没有 sidecar 时再回退到标题识别。
        /// <summary>导入时生成的章节索引；null 表示没有可用索引，阅读端会回退到标题启发式识别。</summary>
        public IReadOnlyList<Chapter> Chapters;
    }


