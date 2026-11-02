using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Termpad;

internal static class Program
{
    private static void Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
            Console.TreatControlCAsInput = true;
        }
        catch
        {
        }

        var editor = new Editor();
        if (args.Length > 0) editor.TryOpen(args[0]);
        editor.Run();
    }
}

internal sealed class Editor
{
    private readonly List<StringBuilder> lines = new() { new StringBuilder() };
    private readonly List<Snapshot> undo = new();
    private readonly List<Snapshot> redo = new();
    private readonly List<string> cutBuffer = new();

    private int row;
    private int col;
    private int topRow;
    private int leftCol;
    private string? path;
    private bool dirty;
    private string status = "";
    private string lastSearch = "";
    private bool lastActionWasCut;
    private bool typingGroup;
    private readonly List<LangState> stateCache = new();
    private Lang stateLang = Lang.Plain;
    private Lang? languageOverride;

    private sealed class Snapshot
    {
        public List<string> Lines = new();
        public int Row;
        public int Col;
        public bool Dirty;
    }

    private enum Lang
    {
        Plain,
        CSharp,
        Python,
        JavaScript,
        Json
    }

    private enum LangState
    {
        Normal,
        BlockComment,
        TripleSingle,
        TripleDouble
    }

    private enum TokenKind
    {
        Plain,
        Keyword,
        String,
        Number,
        Comment,
        Preprocessor
    }

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "and", "as", "async", "await", "base", "bool", "break", "byte", "by", "case",
        "catch", "char", "checked", "class", "const", "continue", "decimal", "default", "delegate",
        "descending", "do", "double", "dynamic", "else", "enum", "equals", "event", "explicit",
        "extern", "false", "finally", "fixed", "float", "for", "foreach", "from", "get", "global",
        "goto", "group", "if", "implicit", "in", "init", "int", "interface", "internal", "into",
        "is", "join", "let", "lock", "long", "nameof", "namespace", "new", "not", "notnull", "null",
        "object", "on", "operator", "or", "orderby", "out", "override", "params", "partial",
        "private", "protected", "public", "readonly", "record", "ref", "remove", "required",
        "return", "sbyte", "sealed", "select", "set", "short", "sizeof", "stackalloc", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "value", "var", "virtual", "void", "volatile",
        "when", "where", "while", "with", "yield"
    };

    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "case",
        "class", "continue", "def", "del", "elif", "else", "except", "finally", "for", "from",
        "global", "if", "import", "in", "is", "lambda", "match", "nonlocal", "not", "or", "pass",
        "raise", "return", "self", "try", "while", "with", "yield"
    };

    private static readonly HashSet<string> JavaScriptKeywords = new(StringComparer.Ordinal)
    {
        "as", "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger",
        "default", "delete", "do", "else", "enum", "export", "extends", "false", "finally", "for",
        "from", "function", "get", "if", "implements", "import", "in", "instanceof", "interface",
        "let", "new", "null", "of", "private", "protected", "public", "readonly", "return", "set",
        "static", "super", "switch", "this", "throw", "true", "try", "type", "typeof", "undefined",
        "var", "void", "while", "with", "yield"
    };

    private static (int Width, int Height) ScreenSize()
    {
        try
        {
            return (Math.Max(10, Console.WindowWidth - 1), Math.Max(5, Console.WindowHeight));
        }
        catch
        {
            return (80, 25);
        }
    }

    public void Run()
    {
        try
        {
            Console.Title = "Блокнот";
            Console.CursorVisible = true;
            Console.Clear();
        }
        catch
        {
        }

        while (true)
        {
            Render();

            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(true);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (!Handle(key)) return;
        }
    }

    private bool Handle(ConsoleKeyInfo key)
    {
        bool previousTyping = typingGroup;
        bool previousCut = lastActionWasCut;
        typingGroup = false;
        lastActionWasCut = false;

        bool alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;
        bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
        bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;

        if (ctrl && alt)
        {
            if (!char.IsControl(key.KeyChar)) InsertChar(key.KeyChar, previousTyping);
            return true;
        }

        if (alt)
        {
            switch (key.Key)
            {
                case ConsoleKey.U:
                    Undo();
                    break;
                case ConsoleKey.E:
                    Redo();
                    break;
                case ConsoleKey.W:
                    RepeatSearch();
                    break;
            }
            return true;
        }

        if (ctrl)
        {
            switch (key.Key)
            {
                case ConsoleKey.G:
                    ShowHelp();
                    break;
                case ConsoleKey.O:
                    Save();
                    break;
                case ConsoleKey.R:
                    Open();
                    break;
                case ConsoleKey.X:
                    return AskExit();
                case ConsoleKey.W:
                    Search();
                    break;
                case ConsoleKey.Oem5:
                case ConsoleKey.Oem102:
                case ConsoleKey.F:
                    Replace();
                    break;
                case ConsoleKey.K:
                    CutLine(previousCut);
                    break;
                case ConsoleKey.U:
                    Paste();
                    break;
                case ConsoleKey.A:
                    col = 0;
                    break;
                case ConsoleKey.E:
                    col = lines[row].Length;
                    break;
                case ConsoleKey.Y:
                    PageBy(-1);
                    break;
                case ConsoleKey.V:
                    PageBy(1);
                    break;
                case ConsoleKey.Z:
                    if (shift) Redo();
                    else Undo();
                    break;
                case ConsoleKey.L:
                    GoToLine();
                    break;
                case ConsoleKey.D:
                    DeleteForward();
                    break;
                case ConsoleKey.H:
                    Backspace();
                    break;
                case ConsoleKey.C:
                    ShowPosition();
                    break;
                case ConsoleKey.S:
                    Save();
                    break;
            }
            return true;
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                MoveUp();
                break;
            case ConsoleKey.DownArrow:
                MoveDown();
                break;
            case ConsoleKey.LeftArrow:
                MoveLeft();
                break;
            case ConsoleKey.RightArrow:
                MoveRight();
                break;
            case ConsoleKey.Home:
                col = 0;
                break;
            case ConsoleKey.End:
                col = lines[row].Length;
                break;
            case ConsoleKey.PageUp:
                PageBy(-1);
                break;
            case ConsoleKey.PageDown:
                PageBy(1);
                break;
            case ConsoleKey.Enter:
                InsertNewLine();
                break;
            case ConsoleKey.Tab:
                InsertText("    ");
                break;
            case ConsoleKey.Backspace:
                Backspace();
                break;
            case ConsoleKey.Delete:
                DeleteForward();
                break;
            case ConsoleKey.F2:
                CycleLanguage();
                break;
            case ConsoleKey.Escape:
                status = "";
                break;
            default:
                if (!char.IsControl(key.KeyChar)) InsertChar(key.KeyChar, previousTyping);
                break;
        }

        return true;
    }

    private void Render()
    {
        var (totalWidth, height) = ScreenSize();
        int viewRows = Math.Max(1, height - 4);
        int statusRow = Math.Max(2, height - 3);
        int helpRow1 = Math.Max(3, height - 2);
        int helpRow2 = Math.Max(4, height - 1);

        int digits = Math.Max(2, lines.Count.ToString().Length);
        int gutterWidth = Math.Min(digits + 3, Math.Max(2, totalWidth - 1));
        int textWidth = Math.Max(1, totalWidth - gutterWidth);
        Lang lang = CurrentLanguage();

        try
        {
            try
            {
                if (OperatingSystem.IsWindows()
                    && (Console.BufferWidth != Console.WindowWidth || Console.BufferHeight != Console.WindowHeight))
                    Console.SetBufferSize(Console.WindowWidth, Console.WindowHeight);
            }
            catch
            {
            }

            if (row < topRow) topRow = row;
            if (row >= topRow + viewRows) topRow = row - viewRows + 1;
            if (col < leftCol) leftCol = col;
            if (col >= leftCol + textWidth) leftCol = col - textWidth + 1;
            if (topRow < 0) topRow = 0;
            if (leftCol < 0) leftCol = 0;

            WriteBar(0, TitleLine(), totalWidth);

            for (int i = 0; i < viewRows; i++)
            {
                int index = topRow + i;
                RenderGutter(index, 1 + i, gutterWidth, digits);
                RenderText(index, 1 + i, gutterWidth, textWidth, lang);
            }

            WriteStatus(StatusLine(), statusRow, totalWidth);
            WriteBar(helpRow1, ShortcutLine1(), totalWidth);
            WriteBar(helpRow2, ShortcutLine2(), totalWidth);

            int cursorY = Math.Clamp(row - topRow, 0, viewRows - 1);
            int cursorX = gutterWidth + Math.Clamp(col - leftCol, 0, textWidth - 1);
            Console.SetCursorPosition(cursorX, 1 + cursorY);
        }
        catch (IOException)
        {
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    private void RenderGutter(int index, int screenRow, int gutterWidth, int digits)
    {
        string number = index < lines.Count ? (index + 1).ToString() : "~";
        string gutter = Fit(number.PadLeft(digits) + " │ ", gutterWidth);

        ConsoleColor old = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.SetCursorPosition(0, screenRow);
        Console.Write(gutter);
        Console.ForegroundColor = old;
    }

    private void RenderText(int index, int screenRow, int gutterWidth, int textWidth, Lang lang)
    {
        Console.SetCursorPosition(gutterWidth, screenRow);

        if (index >= lines.Count || lang == Lang.Plain)
        {
            string plain = "";
            if (index < lines.Count)
            {
                string line = lines[index].ToString();
                if (leftCol < line.Length)
                    plain = line.Substring(leftCol, Math.Min(textWidth, line.Length - leftCol));
            }

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(Fit(plain, textWidth));
            Console.ForegroundColor = ConsoleColor.Gray;
            return;
        }

        string text = lines[index].ToString();
        LangState startState = GetStateAt(index);
        TokenKind[] kinds = Classify(text, lang, startState, out _);

        int position = leftCol;
        int written = 0;

        while (position < text.Length && written < textWidth)
        {
            TokenKind kind = kinds[position];
            int runEnd = position + 1;
            while (runEnd < text.Length && kinds[runEnd] == kind) runEnd++;

            int stop = Math.Min(runEnd, leftCol + textWidth);
            int length = stop - position;
            if (length <= 0) break;

            Console.ForegroundColor = ColorFor(kind);
            Console.Write(text.Substring(position, length));
            written += length;
            position = stop;
        }

        if (written < textWidth)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(new string(' ', textWidth - written));
        }

        Console.ForegroundColor = ConsoleColor.Gray;
    }

    private static ConsoleColor ColorFor(TokenKind kind)
    {
        return kind switch
        {
            TokenKind.Keyword => ConsoleColor.Cyan,
            TokenKind.String => ConsoleColor.Yellow,
            TokenKind.Number => ConsoleColor.Magenta,
            TokenKind.Comment => ConsoleColor.DarkGray,
            TokenKind.Preprocessor => ConsoleColor.Green,
            _ => ConsoleColor.Gray
        };
    }

    private Lang CurrentLanguage()
    {
        return languageOverride ?? AutoLanguage();
    }

    private Lang AutoLanguage()
    {
        if (path is null) return Lang.CSharp;

        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".cs" => Lang.CSharp,
            ".py" => Lang.Python,
            ".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".cjs" => Lang.JavaScript,
            ".json" => Lang.Json,
            _ => Lang.Plain
        };
    }

    private string HighlightLabel()
    {
        if (languageOverride is null)
        {
            Lang auto = AutoLanguage();
            return auto == Lang.Plain ? "авто: нет" : "авто: " + LanguageName(auto);
        }

        return languageOverride == Lang.Plain ? "вручную: нет" : "вручную: " + LanguageName(languageOverride.Value);
    }

    private void CycleLanguage()
    {
        languageOverride = languageOverride switch
        {
            null => Lang.CSharp,
            Lang.CSharp => Lang.Python,
            Lang.Python => Lang.JavaScript,
            Lang.JavaScript => Lang.Json,
            Lang.Json => Lang.Plain,
            _ => null
        };

        stateCache.Clear();
        status = "Подсветка — " + HighlightLabel();
    }

    private static string LanguageName(Lang lang)
    {
        return lang switch
        {
            Lang.CSharp => "C#",
            Lang.Python => "Python",
            Lang.JavaScript => "JS/TS",
            Lang.Json => "JSON",
            _ => "текст"
        };
    }

    private void InvalidateHighlight(int fromLine)
    {
        if (fromLine < 0) fromLine = 0;
        if (fromLine < stateCache.Count)
            stateCache.RemoveRange(fromLine, stateCache.Count - fromLine);
    }

    private LangState GetStateAt(int index)
    {
        Lang lang = CurrentLanguage();
        if (lang != stateLang)
        {
            stateCache.Clear();
            stateLang = lang;
        }

        if (stateCache.Count == 0) stateCache.Add(LangState.Normal);

        while (stateCache.Count <= index && stateCache.Count - 1 < lines.Count)
        {
            int lineIndex = stateCache.Count - 1;
            Classify(lines[lineIndex].ToString(), lang, stateCache[lineIndex], out LangState endState);
            stateCache.Add(endState);
        }

        return index < stateCache.Count ? stateCache[index] : LangState.Normal;
    }

    private static TokenKind[] Classify(string text, Lang lang, LangState state, out LangState endState)
    {
        var kinds = new TokenKind[text.Length];
        endState = state;

        if (lang == Lang.Plain || text.Length == 0) return kinds;

        int firstNonSpace = 0;
        while (firstNonSpace < text.Length && char.IsWhiteSpace(text[firstNonSpace])) firstNonSpace++;

        int i = 0;
        while (i < text.Length)
        {
            if (state == LangState.BlockComment)
            {
                int close = text.IndexOf("*/", i, StringComparison.Ordinal);
                int end = close < 0 ? text.Length : close + 2;
                Paint(kinds, i, end, TokenKind.Comment);
                i = end;
                if (close >= 0) state = LangState.Normal;
                continue;
            }

            if (state == LangState.TripleSingle || state == LangState.TripleDouble)
            {
                string delimiter = state == LangState.TripleSingle ? "'''" : "\"\"\"";
                int close = text.IndexOf(delimiter, i, StringComparison.Ordinal);
                int end = close < 0 ? text.Length : close + 3;
                Paint(kinds, i, end, TokenKind.String);
                i = end;
                if (close >= 0) state = LangState.Normal;
                continue;
            }

            char current = text[i];

            if ((lang == Lang.CSharp || lang == Lang.JavaScript) && current == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    Paint(kinds, i, text.Length, TokenKind.Comment);
                    break;
                }

                if (text[i + 1] == '*')
                {
                    int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    int end = close < 0 ? text.Length : close + 2;
                    Paint(kinds, i, end, TokenKind.Comment);
                    i = end;

                    if (close < 0)
                    {
                        state = LangState.BlockComment;
                        break;
                    }

                    continue;
                }
            }

            if (lang == Lang.CSharp && current == '#' && i == firstNonSpace)
            {
                Paint(kinds, i, text.Length, TokenKind.Preprocessor);
                break;
            }

            if (lang == Lang.Python && current == '#')
            {
                Paint(kinds, i, text.Length, TokenKind.Comment);
                break;
            }

            if (current == '"' || current == '\'')
            {
                if (lang == Lang.Python && i + 2 < text.Length && text[i + 1] == current && text[i + 2] == current)
                {
                    Paint(kinds, i, i + 3, TokenKind.String);
                    state = current == '\'' ? LangState.TripleSingle : LangState.TripleDouble;
                    i += 3;
                    continue;
                }

                bool verbatim = lang == Lang.CSharp && current == '"' && i > 0 && text[i - 1] == '@';
                int end = ScanQuoted(text, i, current, verbatim);
                Paint(kinds, i, end, TokenKind.String);
                i = end;
                continue;
            }

            if (lang == Lang.JavaScript && current == '`')
            {
                int end = ScanQuoted(text, i, current, false);
                Paint(kinds, i, end, TokenKind.String);
                i = end;
                continue;
            }

            if (char.IsDigit(current) || (current == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                int end = ScanNumber(text, i);
                Paint(kinds, i, end, TokenKind.Number);
                i = end;
                continue;
            }

            if (char.IsLetter(current) || current == '_' || current == '@')
            {
                int start = i;
                if (current == '@') i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;

                string word = text.Substring(start, i - start);
                if (IsKeyword(word, lang)) Paint(kinds, start, i, TokenKind.Keyword);
                continue;
            }

            i++;
        }

        endState = state;
        return kinds;
    }

    private static bool IsKeyword(string word, Lang lang)
    {
        return lang switch
        {
            Lang.CSharp => CSharpKeywords.Contains(word),
            Lang.Python => PythonKeywords.Contains(word),
            Lang.JavaScript => JavaScriptKeywords.Contains(word),
            Lang.Json => word == "true" || word == "false" || word == "null",
            _ => false
        };
    }

    private static void Paint(TokenKind[] kinds, int start, int end, TokenKind kind)
    {
        for (int i = start; i < end && i < kinds.Length; i++)
            kinds[i] = kind;
    }

    private static int ScanQuoted(string text, int start, char quote, bool verbatim)
    {
        int i = start + 1;

        while (i < text.Length)
        {
            char current = text[i];

            if (verbatim)
            {
                if (current == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }

                i++;
                continue;
            }

            if (current == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (current == quote) return i + 1;
            i++;
        }

        return text.Length;
    }

    private static int ScanNumber(string text, int start)
    {
        int i = start;

        if (text[i] == '0' && i + 1 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X'))
        {
            i += 2;
            while (i < text.Length && Uri.IsHexDigit(text[i])) i++;
            return i;
        }

        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '_')) i++;

        if (i < text.Length && text[i] == '.')
        {
            i++;
            while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '_')) i++;
        }

        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            int exponent = i + 1;
            if (exponent < text.Length && (text[exponent] == '+' || text[exponent] == '-')) exponent++;

            if (exponent < text.Length && char.IsDigit(text[exponent]))
            {
                i = exponent;
                while (i < text.Length && char.IsDigit(text[i])) i++;
            }
        }

        while (i < text.Length && "fFdDmMuUlL".IndexOf(text[i]) >= 0) i++;
        return i;
    }

    private string TitleLine()
    {
        string name = path is null ? "[без имени]" : Path.GetFileName(path);
        return "  Блокнот — " + name + (dirty ? " (изменён)" : "");
    }

    private string StatusLine()
    {
        string info = "Стр " + (row + 1) + "/" + lines.Count + ", Стлб " + (col + 1) + "   " + HighlightLabel();
        return status.Length > 0 ? " " + status + "    " + info : " " + info;
    }

    private static string ShortcutLine1()
    {
        return " ^G справка   ^O записать   ^R открыть   ^X выход   ^W найти   ^\\ заменить   ^K вырезать   ^U вставить";
    }

    private static string ShortcutLine2()
    {
        return " F2 язык   ^A начало   ^E конец   ^Y вверх   ^V вниз   ^D удалить   ^L к строке   M-U отмена   M-E повтор";
    }

    private static string Fit(string text, int width)
    {
        if (text.Length > width) return text.Substring(0, width);
        return text.PadRight(width);
    }

    private static void WriteBar(int row, string text, int width)
    {
        ConsoleColor foreground = Console.ForegroundColor;
        ConsoleColor background = Console.BackgroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.Gray;
            Console.SetCursorPosition(0, row);
            Console.Write(Fit(text, width));
        }
        finally
        {
            Console.ForegroundColor = foreground;
            Console.BackgroundColor = background;
        }
    }

    private static void WriteStatus(string text, int row, int width)
    {
        Console.SetCursorPosition(0, row);
        Console.Write(Fit(text, width));
    }

    private string? Prompt(string label, string initial = "")
    {
        var input = new StringBuilder(initial);
        var (width, height) = ScreenSize();
        int statusRow = Math.Max(2, height - 3);

        while (true)
        {
            WriteStatus(" " + label + input + "_", statusRow, width);

            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(true);
            }
            catch (InvalidOperationException)
            {
                return null;
            }

            if (key.Key == ConsoleKey.Enter) return input.ToString();
            if (key.Key == ConsoleKey.Escape) return null;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length > 0) input.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                input.Append(key.KeyChar);
            }
        }
    }

    private bool? ConfirmKey(string question)
    {
        var (width, height) = ScreenSize();
        int statusRow = Math.Max(2, height - 3);
        WriteStatus(" " + question, statusRow, width);

        while (true)
        {
            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(true);
            }
            catch (InvalidOperationException)
            {
                return null;
            }

            if (key.Key == ConsoleKey.Y) return true;
            if (key.Key == ConsoleKey.N) return false;
            if (key.Key == ConsoleKey.Escape) return null;
        }
    }

    private void InsertChar(char value, bool previousTyping)
    {
        if (!previousTyping) PushUndo();
        lines[row].Insert(col, value);
        col++;
        dirty = true;
        status = "";
        typingGroup = true;
        InvalidateHighlight(row);
    }

    private void InsertText(string text)
    {
        PushUndo();
        lines[row].Insert(col, text);
        col += text.Length;
        dirty = true;
        status = "";
        InvalidateHighlight(row);
    }

    private void InsertNewLine()
    {
        PushUndo();
        string rest = lines[row].ToString(col, lines[row].Length - col);
        lines[row].Length = col;
        lines.Insert(row + 1, new StringBuilder(rest));
        row++;
        col = 0;
        dirty = true;
        status = "";
        InvalidateHighlight(row);
    }

    private void Backspace()
    {
        if (col > 0)
        {
            PushUndo();
            lines[row].Remove(col - 1, 1);
            col--;
            dirty = true;
            status = "";
            InvalidateHighlight(row);
        }
        else if (row > 0)
        {
            PushUndo();
            int previousLength = lines[row - 1].Length;
            lines[row - 1].Append(lines[row]);
            lines.RemoveAt(row);
            InvalidateHighlight(row);
            row--;
            col = previousLength;
            dirty = true;
            status = "";
        }
    }

    private void DeleteForward()
    {
        if (col < lines[row].Length)
        {
            PushUndo();
            lines[row].Remove(col, 1);
            dirty = true;
            status = "";
            InvalidateHighlight(row);
        }
        else if (row < lines.Count - 1)
        {
            PushUndo();
            lines[row].Append(lines[row + 1]);
            lines.RemoveAt(row + 1);
            dirty = true;
            status = "";
            InvalidateHighlight(row);
        }
    }

    private void MoveUp()
    {
        if (row > 0)
        {
            row--;
            col = Math.Min(col, lines[row].Length);
        }
    }

    private void MoveDown()
    {
        if (row < lines.Count - 1)
        {
            row++;
            col = Math.Min(col, lines[row].Length);
        }
    }

    private void MoveLeft()
    {
        if (col > 0)
        {
            col--;
        }
        else if (row > 0)
        {
            row--;
            col = lines[row].Length;
        }
    }

    private void MoveRight()
    {
        if (col < lines[row].Length)
        {
            col++;
        }
        else if (row < lines.Count - 1)
        {
            row++;
            col = 0;
        }
    }

    private void PageBy(int direction)
    {
        var (_, height) = ScreenSize();
        int viewRows = Math.Max(1, height - 4);
        row = Math.Clamp(row + direction * viewRows, 0, lines.Count - 1);
        col = Math.Min(col, lines[row].Length);
    }

    private void CutLine(bool append)
    {
        PushUndo();

        if (append)
        {
            cutBuffer.Add(lines[row].ToString());
        }
        else
        {
            cutBuffer.Clear();
            cutBuffer.Add(lines[row].ToString());
        }

        lines.RemoveAt(row);
        InvalidateHighlight(row);
        if (lines.Count == 0) lines.Add(new StringBuilder());
        if (row >= lines.Count) row = lines.Count - 1;
        col = Math.Min(col, lines[row].Length);

        lastActionWasCut = true;
        dirty = true;
        status = "Вырезано строк: " + cutBuffer.Count;
    }

    private void Paste()
    {
        if (cutBuffer.Count == 0)
        {
            status = "Буфер пуст";
            return;
        }

        PushUndo();

        StringBuilder current = lines[row];
        string prefix = current.ToString(0, col);
        string suffix = current.ToString(col, current.Length - col);

        current.Length = 0;
        current.Append(prefix);
        current.Append(cutBuffer[0]);

        int lastRow = row;
        for (int i = 1; i < cutBuffer.Count; i++)
        {
            lastRow = row + i;
            lines.Insert(lastRow, new StringBuilder(cutBuffer[i]));
        }

        lines[lastRow].Append(suffix);
        InvalidateHighlight(row);
        row = lastRow;
        col = cutBuffer[cutBuffer.Count - 1].Length;
        dirty = true;
        status = "Вставлено строк: " + cutBuffer.Count;
    }

    private void PushUndo()
    {
        undo.Add(Capture());
        if (undo.Count > 300) undo.RemoveAt(0);
        redo.Clear();
    }

    private Snapshot Capture()
    {
        return new Snapshot
        {
            Lines = lines.Select(line => line.ToString()).ToList(),
            Row = row,
            Col = col,
            Dirty = dirty
        };
    }

    private void Restore(Snapshot snapshot)
    {
        lines.Clear();
        foreach (string line in snapshot.Lines) lines.Add(new StringBuilder(line));
        if (lines.Count == 0) lines.Add(new StringBuilder());

        stateCache.Clear();
        row = Math.Clamp(snapshot.Row, 0, lines.Count - 1);
        col = Math.Clamp(snapshot.Col, 0, lines[row].Length);
        dirty = snapshot.Dirty;
        status = "";
    }

    private void Undo()
    {
        if (undo.Count == 0)
        {
            status = "Отменять нечего";
            return;
        }

        redo.Add(Capture());
        Restore(undo[undo.Count - 1]);
        undo.RemoveAt(undo.Count - 1);
        status = "Отменено";
    }

    private void Redo()
    {
        if (redo.Count == 0)
        {
            status = "Повторять нечего";
            return;
        }

        undo.Add(Capture());
        Restore(redo[redo.Count - 1]);
        redo.RemoveAt(redo.Count - 1);
        status = "Повторено";
    }

    private void Save()
    {
        string? target = Prompt("Имя файла для записи: ", path ?? "");
        if (string.IsNullOrWhiteSpace(target))
        {
            status = "Запись отменена";
            return;
        }

        try
        {
            string full = Path.GetFullPath(target.Trim());
            File.WriteAllText(full, string.Join(Environment.NewLine, lines.Select(line => line.ToString())), new UTF8Encoding(false));
            path = full;
            dirty = false;
            status = "Записано строк: " + lines.Count + " — " + full;
        }
        catch (Exception ex)
        {
            status = "Ошибка: " + ex.Message;
        }
    }

    private void Open()
    {
        if (dirty)
        {
            bool? answer = ConfirmKey("Несохранённые изменения. Открыть другой файл? (Y/N)");
            if (answer != true) return;
        }

        string? target = Prompt("Открыть файл: ", path ?? "");
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            string full = Path.GetFullPath(target.Trim());
            LoadText(File.ReadAllText(full));
            path = full;
            dirty = false;
            status = "Открыто: " + full;
        }
        catch (Exception ex)
        {
            status = "Ошибка: " + ex.Message;
        }
    }

    public void TryOpen(string file)
    {
        try
        {
            string full = Path.GetFullPath(file);
            LoadText(File.ReadAllText(full));
            path = full;
            dirty = false;
        }
        catch (Exception ex)
        {
            status = "Ошибка: " + ex.Message;
        }
    }

    private void LoadText(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");

        lines.Clear();
        foreach (string raw in text.Split('\n'))
        {
            var line = new StringBuilder(raw.Length);
            foreach (char value in raw)
                line.Append(char.IsControl(value) ? ' ' : value);
            lines.Add(line);
        }

        if (lines.Count == 0) lines.Add(new StringBuilder());

        row = 0;
        col = 0;
        topRow = 0;
        leftCol = 0;
        undo.Clear();
        redo.Clear();
        stateCache.Clear();
        typingGroup = false;
        status = "";
    }

    private bool TryFindFrom(int startRow, int startCol, string query, out int foundRow, out int foundCol)
    {
        foundRow = -1;
        foundCol = -1;

        for (int r = startRow; r < lines.Count; r++)
        {
            string text = lines[r].ToString();
            int from = r == startRow ? Math.Min(startCol, text.Length) : 0;
            int index = text.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                foundRow = r;
                foundCol = index;
                return true;
            }
        }

        return false;
    }

    private bool FindNext(string query, out int foundRow, out int foundCol)
    {
        if (TryFindFrom(row, col + 1, query, out foundRow, out foundCol)) return true;
        if (TryFindFrom(0, 0, query, out foundRow, out foundCol)) return true;
        return false;
    }

    private void Search()
    {
        string? query = Prompt("Искать: ", lastSearch);
        if (string.IsNullOrWhiteSpace(query)) return;

        lastSearch = query;

        if (!FindNext(lastSearch, out int foundRow, out int foundCol))
        {
            status = "Не найдено: " + lastSearch;
            return;
        }

        row = foundRow;
        col = foundCol;
        status = "Найдено: " + lastSearch;
    }

    private void RepeatSearch()
    {
        if (lastSearch.Length == 0)
        {
            status = "Нет предыдущего поиска";
            return;
        }

        if (!FindNext(lastSearch, out int foundRow, out int foundCol))
        {
            status = "Не найдено: " + lastSearch;
            return;
        }

        row = foundRow;
        col = foundCol;
        status = "Найдено: " + lastSearch;
    }

    private void Replace()
    {
        string? query = Prompt("Заменить: ", lastSearch);
        if (string.IsNullOrWhiteSpace(query)) return;

        string? replacement = Prompt("На: ", "");
        if (replacement is null) return;

        lastSearch = query;

        int scanRow = row;
        int scanCol = col;
        int count = 0;
        bool pushed = false;

        while (TryFindFrom(scanRow, scanCol, query, out int foundRow, out int foundCol))
        {
            row = foundRow;
            col = foundCol;
            Render();

            bool? answer = ConfirmKey("Заменить «" + query + "»? (Y — да, N — пропустить, Esc — стоп)");
            if (answer is null) break;

            if (answer == true)
            {
                if (!pushed)
                {
                    PushUndo();
                    pushed = true;
                }

                lines[row].Remove(col, query.Length);
                lines[row].Insert(col, replacement);
                scanRow = row;
                scanCol = col + replacement.Length;
                count++;
                dirty = true;
            }
            else
            {
                scanRow = row;
                scanCol = col + query.Length;
            }

            row = scanRow;
            col = scanCol;
            status = "Заменено: " + count;
        }

        status = "Заменено: " + count;
    }

    private void GoToLine()
    {
        string? input = Prompt("Перейти к строке: ", (row + 1).ToString());
        if (input is null) return;

        if (!int.TryParse(input, out int number))
        {
            status = "Нужен номер строки";
            return;
        }

        row = Math.Clamp(number - 1, 0, lines.Count - 1);
        col = 0;
        status = "Строка " + (row + 1);
    }

    private void ShowPosition()
    {
        int characters = lines.Sum(line => line.Length) + Math.Max(0, lines.Count - 1);
        status = "Стр " + (row + 1) + "/" + lines.Count + ", Стлб " + (col + 1) + ", символов: " + characters;
    }

    private void ShowHelp()
    {
        string[] help =
        {
            "Блокнот — справка",
            "",
            "  ^G      эта справка            ^O   записать файл",
            "  ^R      открыть файл           ^X   выход",
            "  ^W      поиск                  M-W  искать далее",
            "  ^\\      замена                 ^L   перейти к строке",
            "  ^K      вырезать строку        ^U   вставить",
            "  ^A      начало строки          ^E   конец строки",
            "  ^Y      страница вверх         ^V   страница вниз",
            "  ^D      удалить символ         ^H   стереть символ",
            "  ^C      позиция курсора",
            "  M-U     отмена                 M-E  повтор",
            "",
            "  Стрелки, Home/End, PgUp/PgDn, Enter, Tab, Backspace, Del.",
            "  Подсветка по расширению: .cs, .py, .js/.ts, .json. Номера строк — слева.",
            "  F2      язык подсветки: авто/C#/Python/JS/JSON/нет",
            "",
            "Нажмите любую клавишу для возврата."
        };

        try
        {
            Console.Clear();
            int y = 0;
            foreach (string line in help)
            {
                if (y >= Console.WindowHeight) break;
                Console.SetCursorPosition(0, y++);
                Console.Write(line);
            }

            Console.ReadKey(true);
            Console.Clear();
        }
        catch (Exception)
        {
        }
    }

    private bool AskExit()
    {
        if (!dirty) return false;

        bool? answer = ConfirmKey("Сохранить изменённый буфер? (Y — да, N — нет, Esc — отмена)");
        if (answer is null) return true;

        if (answer == true)
        {
            Save();
            return dirty;
        }

        return false;
    }
}
