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
    public sealed class ImportedBook
    {
        public string Title, Text, Format;
        // EPUB 导入时从书内 nav/NCX 目录生成的章节索引；TXT 为 null，
        // 由阅读端按标题启发式识别。随书保存为 books/<id>.chapters.tsv。
        public IReadOnlyList<Chapter> Chapters;
    }


