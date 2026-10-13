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
    public sealed class Book
    {
        public string Id, Title, Format;
        public int Position, Bookmark = -1, Length;
        public bool Completed;
        public bool IsPinned;
        public string Group = "";
        public override string ToString() { return Title; }
    }


