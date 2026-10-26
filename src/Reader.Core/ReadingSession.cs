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
    /// 一本书的阅读会话：持有正文与阅读位置，按“是否放得下”的度量函数完成分页和翻页，
    /// 并维护向前翻页的历史。所有位置都是 UTF-16 字符下标，且不会落在代理对中间。
    /// </summary>
    public sealed class ReadingSession
    {
        /// <summary>当前书籍元数据；阅读位置直接读写其中的 <see cref="Book.Position"/>。</summary>
        public readonly Book Book;
        /// <summary>整本书的规范化纯文本，构造后不再变化。</summary>
        public readonly string Text;
        /// <summary>当前阅读位置（UTF-16 下标），等同于 <see cref="Book.Position"/>。</summary>
        public int Position { get { return Book.Position; } }
        // “上一页”的精确落点栈；任意 Seek 都会清空，避免历史与当前位置错位。
        private readonly Stack<int> history = new Stack<int>();
        /// <summary>为指定书籍创建阅读会话，并校验正文非空。</summary>
        /// <param name="book">书籍元数据；构造时会写入 <see cref="Book.Length"/> 并规范化阅读位置。</param>
        /// <param name="text">该书正文，不能为空。</param>
        /// <exception cref="InvalidDataException">正文为 null 或空字符串。</exception>
        public ReadingSession(Book book, string text)
        {
            if (String.IsNullOrEmpty(text)) throw new InvalidDataException("书籍正文为空，请重新导入。");
            // 先暂存“已读完”，因为 Seek 会重置它；仅打开书不应改变阅读完成状态。
            Book = book; Text = text; Book.Length = text.Length; bool completed = book.Completed; Seek(book.Position); book.Completed = completed;
        }
        /// <summary>跳转到指定位置；越界会夹取，落在换行或代理对中间时向前调整。</summary>
        /// <param name="position">目标字符下标，超出正文范围时自动夹取。</param>
        /// <remarks>跳转会清空向前翻页历史并取消“已读完”状态。</remarks>
        public void Seek(int position)
        {
            // 不允许落在代理对中间，否则按字符截取时会切出半个字符。
            Book.Position = Store.Clamp(position, 0, Math.Max(0, Text.Length - 1));
            if (Book.Position > 0 && Char.IsLowSurrogate(Text[Book.Position])) Book.Position--;
            // 跳过换行，避免新页面以空行开头。
            while (Book.Position < Text.Length - 1 && Text[Book.Position] == '\n') Book.Position++;
            // 任意跳转都会使旧的“上一页”历史失效。
            history.Clear(); Book.Completed = false;
        }
        /// <summary>计算从当前位置开始、能放进一行的结束位置。</summary>
        /// <param name="fits">度量函数：给定候选文本，返回是否放得下。</param>
        /// <returns>该行结束后的字符下标（不含）。</returns>
        public int End(Func<string, bool> fits)
        {
            return EndAt(Position, fits);
        }
        // 二分查找最大可容纳长度：fits 对前缀长度单调，可把逐字测量降为 O(log n)。
        // 测量范围限制在当前段落且不超过 2048 字符，避免超长段落反复测量整段。
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
            // 不从代理对中间切开，否则返回的位置会指向半个字符。
            if (end < Text.Length && end > position && Char.IsHighSurrogate(Text[end - 1]) && Char.IsLowSurrogate(Text[end])) end--;
            // 度量函数连一个字符都放不下时强制前进，避免翻页卡在原地。
            if (end == position) end = Math.Min(Text.Length, position + 2);
            return end;
        }
        /// <summary>从当前位置开始排一页。</summary>
        /// <param name="fits">行宽度量函数。</param>
        /// <param name="lineCount">本页行数，内部夹取到 1–10。</param>
        /// <returns>本页各行文本与下一页起点。</returns>
        public ReadingPage Layout(Func<string, bool> fits, int lineCount)
        {
            ReadingPage page = new ReadingPage();
            int position = Position;
            for (int row = 0; row < Store.Clamp(lineCount, 1, 10) && position < Text.Length; row++)
            {
                // 行首空行不占显示行，直接跳过。
                while (position < Text.Length && Text[position] == '\n') position++;
                if (position >= Text.Length) break;
                // 每行单独测量，避免把整页当成一个字符串反复扫描。
                page.End = EndAt(position, fits);
                page.Lines.Add(Text.Substring(position, page.End - position));
                position = page.End;
            }
            return page;
        }
        /// <summary>清空向前翻页历史。</summary>
        public void ClearHistory() { history.Clear(); }
        /// <summary>翻到下一页。</summary>
        /// <param name="end">当前页的结束位置，通常来自 <see cref="ReadingPage.End"/>。</param>
        /// <returns>是否真的前进了；已到正文末尾时返回 false 并标记已读完。</returns>
        public bool Next(int end)
        {
            // 跳过页间换行，避免停在空行上。
            while (end < Text.Length && Text[end] == '\n') end++;
            if (end >= Text.Length) { Book.Completed = true; return false; }
            // 防止空页或异常度量导致位置不前进。
            if (end <= Position) return false;
            Book.Completed = false;
            history.Push(Position); Book.Position = end; return true;
        }
        /// <summary>向回翻一页（默认一行）。</summary>
        /// <param name="fits">行宽度量函数。</param>
        public void Previous(Func<string, bool> fits)
        {
            Previous(fits, 1);
        }
        /// <summary>向回翻一页；有历史时优先按历史回退，否则按度量反推上一页起点。</summary>
        /// <param name="fits">行宽度量函数。</param>
        /// <param name="lineCount">要回退的行数，内部夹取到 1–10。</param>
        public void Previous(Func<string, bool> fits, int lineCount)
        {
            // 用户主动回翻即取消“已读完”状态。
            Book.Completed = false;
            // 历史记录的是精确落点，比重新测量更可靠。
            if (history.Count > 0) { Book.Position = history.Pop(); return; }
            for (int row = 0; row < Store.Clamp(lineCount, 1, 10) && Position > 0; row++) PreviousLine(fits);
        }
        // 反向二分：在当前段落最后 2048 字符内找最靠左、但仍能放下的起点。
        // fits 对起点越靠右越容易成立，所以判定成功时继续向左收缩。
        private void PreviousLine(Func<string, bool> fits)
        {
            if (Position == 0) return;
            // 跳过紧邻当前位置的换行，定位到上一段最后一个字符之后。
            int end = Position;
            while (end > 0 && Text[end - 1] == '\n') end--;
            // 只在当前段落内查找，避免跨段取到过长的行。
            int paragraphStart = Text.LastIndexOf('\n', Math.Max(0, end - 1)) + 1;
            int low = Math.Max(paragraphStart, end - 2048), high = end - 1, best = high;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                if (fits(Text.Substring(mid, end - mid))) { best = mid; high = mid - 1; }
                else low = mid + 1;
            }
            Book.Position = best;
            // 起点同样不能落在代理对中间。
            if (Book.Position > 0 && Char.IsLowSurrogate(Text[Book.Position])) Book.Position--;
        }
        /// <summary>阅读进度百分比（0–100）；已读完固定为 100。</summary>
        public int Percent { get { return Book.Completed ? 100 : (Text.Length <= 1 ? 0 : (int)(100.0 * Position / (Text.Length - 1))); } }
    }


