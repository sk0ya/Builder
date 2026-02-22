using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Builder;

public readonly record struct AnsiSpan(string Text, Color? Foreground, Color? Background, bool Bold);

public static partial class AnsiParser
{
    [GeneratedRegex(@"\x1b\[([0-9;]*)m")]
    private static partial Regex AnsiEscapeRegex();

    // dotnet / MSBuild 向けフォールバックパターン（ANSI未出力時に使用）
    [GeneratedRegex(@":\s*error\s+[A-Z]+\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompilerErrorPattern();
    [GeneratedRegex(@"^error\s+[A-Z]+\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorStartPattern();
    [GeneratedRegex(@":\s*warning\s+[A-Z]+\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompilerWarningPattern();
    [GeneratedRegex(@"^warning\s+[A-Z]+\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarningStartPattern();
    [GeneratedRegex(@"\d+\s+error\(s\)", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorCountPattern();
    [GeneratedRegex(@"\d+\s+warning\(s\)", RegexOptions.IgnoreCase)]
    private static partial Regex WarningCountPattern();

    // VSCode Dark+ palette
    private static readonly Color[] StandardColors =
    [
        Color.FromRgb(0x1E, 0x1E, 0x1E), // 0 Black
        Color.FromRgb(0xCD, 0x31, 0x31), // 1 Red
        Color.FromRgb(0x0D, 0xBC, 0x79), // 2 Green
        Color.FromRgb(0xE5, 0xC0, 0x7B), // 3 Yellow
        Color.FromRgb(0x2F, 0x9E, 0xE5), // 4 Blue
        Color.FromRgb(0xBC, 0x3F, 0xBC), // 5 Magenta
        Color.FromRgb(0x11, 0xA8, 0xCD), // 6 Cyan
        Color.FromRgb(0xE5, 0xE5, 0xE5), // 7 White
    ];

    private static readonly Color[] BrightColors =
    [
        Color.FromRgb(0x66, 0x66, 0x66), // 0 Bright Black (dark gray)
        Color.FromRgb(0xF1, 0x44, 0x44), // 1 Bright Red
        Color.FromRgb(0x23, 0xD1, 0x8B), // 2 Bright Green
        Color.FromRgb(0xF5, 0xF5, 0x43), // 3 Bright Yellow
        Color.FromRgb(0x3B, 0x8E, 0xEA), // 4 Bright Blue
        Color.FromRgb(0xD6, 0x70, 0xD6), // 5 Bright Magenta
        Color.FromRgb(0x29, 0xB8, 0xDB), // 6 Bright Cyan
        Color.FromRgb(0xFF, 0xFF, 0xFF), // 7 Bright White
    ];

    /// <summary>ステートレス版（1行完結の場合に使用）。</summary>
    public static List<AnsiSpan> Parse(string text)
    {
        Color? fg = null;
        Color? bg = null;
        bool bold = false;
        return ParseWithState(text, ref fg, ref bg, ref bold);
    }

    /// <summary>
    /// ステートフル版。呼び出し前後で fg/bg/bold を引き継ぐため、
    /// ESC コードと表示テキストが別行に分かれる出力でも色が正しく適用される。
    /// </summary>
    internal static List<AnsiSpan> ParseWithState(string text, ref Color? fg, ref Color? bg, ref bool bold)
    {
        var result = new List<AnsiSpan>();
        int lastIndex = 0;

        foreach (Match match in AnsiEscapeRegex().Matches(text))
        {
            if (match.Index > lastIndex)
                result.Add(new AnsiSpan(text[lastIndex..match.Index], fg, bg, bold));

            var param = match.Groups[1].Value;
            if (param.Length == 0)
            {
                fg = null; bg = null; bold = false;
            }
            else
            {
                var codes = param.Split(';', StringSplitOptions.RemoveEmptyEntries);
                ProcessCodes(codes, ref fg, ref bg, ref bold);
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
            result.Add(new AnsiSpan(text[lastIndex..], fg, bg, bold));

        return result;
    }

    private static void ProcessCodes(string[] codes, ref Color? fg, ref Color? bg, ref bool bold)
    {
        int i = 0;
        while (i < codes.Length)
        {
            if (!int.TryParse(codes[i], out int code)) { i++; continue; }

            switch (code)
            {
                case 0: fg = null; bg = null; bold = false; break;
                case 1: bold = true; break;
                case 2: bold = false; break; // dim / normal weight
                case 22: bold = false; break;
                case >= 30 and <= 37: fg = StandardColors[code - 30]; break;
                case 38:
                    if (i + 2 < codes.Length && codes[i + 1] == "5")
                    {
                        if (int.TryParse(codes[i + 2], out int idx)) fg = Get256Color(idx);
                        i += 2;
                    }
                    else if (i + 4 < codes.Length && codes[i + 1] == "2")
                    {
                        if (int.TryParse(codes[i + 2], out int r) &&
                            int.TryParse(codes[i + 3], out int gv) &&
                            int.TryParse(codes[i + 4], out int b))
                            fg = Color.FromRgb((byte)r, (byte)gv, (byte)b);
                        i += 4;
                    }
                    break;
                case 39: fg = null; break;
                case >= 40 and <= 47: bg = StandardColors[code - 40]; break;
                case 48:
                    if (i + 2 < codes.Length && codes[i + 1] == "5")
                    {
                        if (int.TryParse(codes[i + 2], out int idx)) bg = Get256Color(idx);
                        i += 2;
                    }
                    else if (i + 4 < codes.Length && codes[i + 1] == "2")
                    {
                        if (int.TryParse(codes[i + 2], out int r) &&
                            int.TryParse(codes[i + 3], out int gv) &&
                            int.TryParse(codes[i + 4], out int b))
                            bg = Color.FromRgb((byte)r, (byte)gv, (byte)b);
                        i += 4;
                    }
                    break;
                case 49: bg = null; break;
                case >= 90 and <= 97: fg = BrightColors[code - 90]; break;
                case >= 100 and <= 107: bg = BrightColors[code - 100]; break;
            }
            i++;
        }
    }

    private static Color Get256Color(int index)
    {
        if (index < 8) return StandardColors[index];
        if (index < 16) return BrightColors[index - 8];
        if (index < 232)
        {
            index -= 16;
            int b = index % 6;
            int g = (index / 6) % 6;
            int r = index / 36;
            return Color.FromRgb(
                (byte)(r == 0 ? 0 : 55 + r * 40),
                (byte)(g == 0 ? 0 : 55 + g * 40),
                (byte)(b == 0 ? 0 : 55 + b * 40));
        }
        int gray = (index - 232) * 10 + 8;
        return Color.FromRgb((byte)gray, (byte)gray, (byte)gray);
    }

    /// <summary>
    /// ANSIコードを含まない行にキーワードベースのフォールバック色を返す。
    /// dotnet build などリダイレクト時にANSI出力しないツール向け。
    /// </summary>
    public static Color? GetFallbackColor(string line)
    {
        var t = line.TrimStart();

        // アプリ自身のメッセージ
        if (t.StartsWith("[エラー]") || t.StartsWith("[ERR] "))
            return Color.FromRgb(0xF4, 0x43, 0x36);
        if (t.StartsWith("[完了]"))
            return Color.FromRgb(0x4C, 0xAF, 0x50);
        if (t.StartsWith("[キャンセル]"))
            return Color.FromRgb(0xFF, 0xA0, 0x00);

        // dotnet / MSBuild ビルド結果
        if (t.StartsWith("Build succeeded", StringComparison.OrdinalIgnoreCase))
            return Color.FromRgb(0x4C, 0xAF, 0x50);
        if (t.StartsWith("Build FAILED", StringComparison.OrdinalIgnoreCase))
            return Color.FromRgb(0xF4, 0x43, 0x36);

        // コンパイラエラー: "(行,列): error CS1234:" または "error CS1234:"
        if (CompilerErrorPattern().IsMatch(t) || ErrorStartPattern().IsMatch(t))
            return Color.FromRgb(0xF4, 0x43, 0x36);

        // コンパイラ警告: "(行,列): warning CS1234:" または "warning CS1234:"
        if (CompilerWarningPattern().IsMatch(t) || WarningStartPattern().IsMatch(t))
            return Color.FromRgb(0xFF, 0xA0, 0x00);

        // サマリー行: "X Error(s)" "X Warning(s)"
        if (ErrorCountPattern().IsMatch(t))
            return Color.FromRgb(0xF4, 0x43, 0x36);
        if (WarningCountPattern().IsMatch(t))
            return Color.FromRgb(0xFF, 0xA0, 0x00);

        return null;
    }
}

/// <summary>
/// 複数行にまたがる ANSI エスケープ状態を保持するクラス。
/// ストリーミング出力で ESC コードと表示テキストが別行になる場合でも
/// 正しく色が引き継がれる。
/// </summary>
public class AnsiState
{
    private Color? _fg;
    private Color? _bg;
    private bool _bold;

    /// <summary>前の行からアクティブな色/スタイルが引き継がれているかどうか。</summary>
    public bool HasActiveState => _fg.HasValue || _bg.HasValue || _bold;

    /// <summary>状態をリセットする（プロジェクト切替・ログクリア時に呼ぶ）。</summary>
    public void Reset()
    {
        _fg = null;
        _bg = null;
        _bold = false;
    }

    /// <summary>1行分を解析し、状態を次の行へ引き継ぐ。</summary>
    public List<AnsiSpan> Parse(string text)
        => AnsiParser.ParseWithState(text, ref _fg, ref _bg, ref _bold);
}
