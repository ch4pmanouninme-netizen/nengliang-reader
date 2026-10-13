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
    public sealed class ReadingSession
    {
        public readonly Book Book;
        public readonly string Text;
        public int Position { get { return Book.Position; } }
        private readonly Stack<int> history = new Stack<int>();
        public ReadingSession(Book book, string text)
        {
            if (String.IsNullOrEmpty(text)) throw new InvalidDataException("书籍正文为空，请重新导入。");
            Book = book; Text = text; Book.Length = text.Length; bool completed = book.Completed; Seek(book.Position); book.Completed = completed;
        }
        public void Seek(int position)
        {
            Book.Position = Store.Clamp(position, 0, Math.Max(0, Text.Length - 1));
            if (Book.Position > 0 && Char.IsLowSurrogate(Text[Book.Position])) Book.Position--;
            while (Book.Position < Text.Length - 1 && Text[Book.Position] == '\n') Book.Position++;
            history.Clear(); Book.Completed = false;
        }
        public int End(Func<string, bool> fits)
        {
            return EndAt(Position, fits);
        }
        private int EndAt(int position, Func<string, bool> fits)
        {
            int paragraphEnd = Text.IndexOf('\n', position);
            if (paragraphEnd < 0) paragraphEnd = Text.Length;
            int low = 1, high = Math.Min(paragraphEnd - position, 2048), best = 1;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                if (fits(Text.Substring(position, mid))) { best = mid; low = mid + 1; }
                else high = mid - 1;
            }
            int end = Math.Min(paragraphEnd, position + best);
            if (end < Text.Length && end > position && Char.IsHighSurrogate(Text[end - 1]) && Char.IsLowSurrogate(Text[end])) end--;
            if (end == position) end = Math.Min(Text.Length, position + 2);
            return end;
        }
        public ReadingPage Layout(Func<string, bool> fits, int lineCount)
        {
            ReadingPage page = new ReadingPage();
            int position = Position;
            for (int row = 0; row < Store.Clamp(lineCount, 1, 10) && position < Text.Length; row++)
            {
                while (position < Text.Length && Text[position] == '\n') position++;
                if (position >= Text.Length) break;
                page.End = EndAt(position, fits);
                page.Lines.Add(Text.Substring(position, page.End - position));
                position = page.End;
            }
            return page;
        }
        public void ClearHistory() { history.Clear(); }
        public bool Next(int end)
        {
            while (end < Text.Length && Text[end] == '\n') end++;
            if (end >= Text.Length) { Book.Completed = true; return false; }
            if (end <= Position) return false;
            Book.Completed = false;
            history.Push(Position); Book.Position = end; return true;
        }
        public void Previous(Func<string, bool> fits)
        {
            Previous(fits, 1);
        }
        public void Previous(Func<string, bool> fits, int lineCount)
        {
            Book.Completed = false;
            if (history.Count > 0) { Book.Position = history.Pop(); return; }
            for (int row = 0; row < Store.Clamp(lineCount, 1, 10) && Position > 0; row++) PreviousLine(fits);
        }
        private void PreviousLine(Func<string, bool> fits)
        {
            if (Position == 0) return;
            int end = Position;
            while (end > 0 && Text[end - 1] == '\n') end--;
            int paragraphStart = Text.LastIndexOf('\n', Math.Max(0, end - 1)) + 1;
            int low = Math.Max(paragraphStart, end - 2048), high = end - 1, best = high;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                if (fits(Text.Substring(mid, end - mid))) { best = mid; high = mid - 1; }
                else low = mid + 1;
            }
            Book.Position = best;
            if (Book.Position > 0 && Char.IsLowSurrogate(Text[Book.Position])) Book.Position--;
        }
        public int Percent { get { return Book.Completed ? 100 : (Text.Length <= 1 ? 0 : (int)(100.0 * Position / (Text.Length - 1))); } }
    }


