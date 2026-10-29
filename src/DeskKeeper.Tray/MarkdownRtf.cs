using System.Text;
using System.Text.RegularExpressions;

namespace DeskKeeper.Tray;

/// <summary>
/// 메모용 마크다운 일부를 RichTextBox 가 그리는 RTF 로 바꾼다(보기 전용, 저장은 늘 마크다운 원문).
/// 지원: # 제목(1~3), **굵게**, *기울임*·_기울임_, ~~취소선~~, `코드`, ``` 코드 블록,
/// - · * 목록(앞 공백 2칸마다 한 단계), 1. 번호, - [ ] · - [x] 할 일, > 인용, --- 구분선, [글](주소).
/// 표·이미지·HTML 은 글자 그대로 보인다.
/// </summary>
static partial class MarkdownRtf
{
    // 색 번호(colortbl): 1 글자, 2 흐린 글자, 3 강조(청록), 4 코드 바탕, 5 구분선
    private const string Header =
        @"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil Segoe UI;}{\f1\fmodern Consolas;}{\f2\fnil Segoe UI Symbol;}}" +
        @"{\colortbl;\red26\green26\blue26;\red96\green96\blue96;\red13\green148\blue136;\red240\green240\blue240;\red200\green200\blue200;}" +
        @"\viewkind4\uc1";

    /// <summary>본문 글자 크기(반 포인트 단위, 20 = 10pt).</summary>
    private const int BodySize = 20;

    public static string Render(string markdown)
    {
        var rtf = new StringBuilder(Header);
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        bool inCode = false;
        foreach (var raw in lines)
        {
            if (raw.TrimStart().StartsWith("```"))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                // 코드 블록: 고정폭, 회색 바탕, 서식 해석 없이 그대로
                rtf.Append(@"\pard\li240\sa0\f1\fs18\cf1\highlight4 ");
                AppendText(rtf, raw.Length == 0 ? " " : raw);
                rtf.Append(@"\highlight0\par");
                continue;
            }
            AppendLine(rtf, raw);
        }
        rtf.Append('}');
        return rtf.ToString();
    }

    private static void AppendLine(StringBuilder rtf, string raw)
    {
        var line = raw.TrimEnd();
        if (line.Length == 0)
        {
            rtf.Append(@"\pard\sa0\f0\fs" + BodySize / 2 + @" \par");
            return;
        }

        // 제목
        var heading = HeadingRegex().Match(line);
        if (heading.Success)
        {
            int level = heading.Groups[1].Length;
            int size = level switch { 1 => 32, 2 => 26, _ => 22 };
            int before = level == 1 ? 120 : 80;
            rtf.Append($@"\pard\sb{before}\sa60\f0\fs{size}\b\cf1 ");
            AppendInline(rtf, heading.Groups[2].Value);
            rtf.Append(@"\b0\par");
            return;
        }

        // 구분선
        if (RuleRegex().IsMatch(line))
        {
            rtf.Append(@"\pard\sa60\f0\fs" + BodySize + @"\cf5 ");
            AppendText(rtf, new string('─', 40));
            rtf.Append(@"\cf1\par");
            return;
        }

        // 인용
        if (line.StartsWith('>'))
        {
            rtf.Append(@"\pard\li360\sa60\f0\fs" + BodySize + @"\cf2\i ");
            AppendInline(rtf, line.TrimStart('>').TrimStart());
            rtf.Append(@"\i0\cf1\par");
            return;
        }

        // 목록(할 일·글머리·번호). 앞 공백 2칸 = 한 단계
        var item = ListRegex().Match(line);
        if (item.Success)
        {
            int indent = item.Groups[1].Value.Replace("\t", "  ").Length / 2;
            string marker = item.Groups[2].Value;
            string body = item.Groups[3].Value;
            string bullet;
            var task = TaskRegex().Match(body);
            if (task.Success)
            {
                bullet = task.Groups[1].Value.Trim().Length > 0 ? "☑" : "☐";
                body = task.Groups[2].Value;
            }
            else
            {
                bullet = char.IsDigit(marker[0]) ? marker : indent % 2 == 0 ? "•" : "◦";
            }
            int left = 360 + indent * 360;
            rtf.Append($@"\pard\li{left}\fi-280\tx{left}\sa40\f0\fs{BodySize}\cf1 ");
            if (task.Success)
                rtf.Append(@"{\f2 ");
            AppendText(rtf, bullet);
            if (task.Success)
                rtf.Append('}');
            rtf.Append(@"\tab ");
            // 끝낸 할 일은 흐리게
            if (bullet == "☑")
                rtf.Append(@"\cf2 ");
            AppendInline(rtf, body);
            rtf.Append(@"\cf1\par");
            return;
        }

        rtf.Append(@"\pard\sa60\f0\fs" + BodySize + @"\cf1 ");
        AppendInline(rtf, line);
        rtf.Append(@"\par");
    }

    /// <summary>줄 안의 서식: 코드 → 링크 → 굵게 → 취소선 → 기울임 순으로 가장 먼저 나오는 것을 처리한다.</summary>
    private static void AppendInline(StringBuilder rtf, string text)
    {
        int pos = 0;
        foreach (Match m in InlineRegex().Matches(text))
        {
            AppendText(rtf, text[pos..m.Index]);
            if (m.Groups["code"].Success)
            {
                rtf.Append(@"{\f1\fs18\highlight4 ");
                AppendText(rtf, m.Groups["code"].Value);
                rtf.Append('}');
            }
            else if (m.Groups["label"].Success)
            {
                rtf.Append(@"{\cf3\ul ");
                AppendText(rtf, m.Groups["label"].Value);
                rtf.Append(@"}{\cf2  (");
                AppendText(rtf, m.Groups["url"].Value);
                rtf.Append(")}");
            }
            else if (m.Groups["bold"].Success)
            {
                rtf.Append(@"{\b ");
                AppendInline(rtf, m.Groups["bold"].Value);
                rtf.Append('}');
            }
            else if (m.Groups["strike"].Success)
            {
                rtf.Append(@"{\strike\cf2 ");
                AppendInline(rtf, m.Groups["strike"].Value);
                rtf.Append('}');
            }
            else
            {
                var italic = m.Groups["italic"].Success ? m.Groups["italic"].Value : m.Groups["italic2"].Value;
                rtf.Append(@"{\i ");
                AppendInline(rtf, italic);
                rtf.Append('}');
            }
            pos = m.Index + m.Length;
        }
        AppendText(rtf, text[pos..]);
    }

    /// <summary>RTF 특수 문자와 ASCII 밖 글자(한글 등)를 \uN? 로 적는다.</summary>
    private static void AppendText(StringBuilder rtf, string text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': rtf.Append(@"\\"); break;
                case '{': rtf.Append(@"\{"); break;
                case '}': rtf.Append(@"\}"); break;
                case '\t': rtf.Append(@"\tab "); break;
                default:
                    if (c < 0x80)
                        rtf.Append(c);
                    else
                        rtf.Append(@"\u").Append((short)c).Append('?');
                    break;
            }
        }
    }

    [GeneratedRegex(@"^(#{1,3})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^([ \t]*)([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListRegex();

    [GeneratedRegex(@"^\[([ xX])\]\s*(.*)$")]
    private static partial Regex TaskRegex();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)\)|\*\*(?<bold>.+?)\*\*|~~(?<strike>.+?)~~|(?<![\w*])\*(?<italic>[^*\s][^*]*?)\*(?![\w*])|(?<![\w])_(?<italic2>[^_\s][^_]*?)_(?![\w])")]
    private static partial Regex InlineRegex();
}
