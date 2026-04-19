using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Chandra.Ocr.Output;

public static class MarkdownConverter
{
    public static string ParseMarkdown(
        string html, bool includeHeadersFooters = false, bool includeImages = true)
    {
        var processedHtml = HtmlParser.ParseHtml(html, includeHeadersFooters, includeImages);
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(processedHtml);
            var sb = new StringBuilder();
            Convert(doc.DocumentNode, sb, new Context());
            return PostClean(sb.ToString()).Trim();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error converting HTML to Markdown: {e}");
            return string.Empty;
        }
    }

    private class Context
    {
        public int ListDepth;
        public string? ListType;
        public int OrderedIdx;
    }

    private static void Convert(HtmlNode node, StringBuilder sb, Context ctx)
    {
        foreach (var child in node.ChildNodes)
            ConvertNode(child, sb, ctx);
    }

    private static void ConvertNode(HtmlNode node, StringBuilder sb, Context ctx)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            string text = HtmlEntity.DeEntitize(node.InnerText);
            if (!IsInside(node, "pre", "code", "kbd", "samp", "math"))
                text = Escape(text);
            if (!IsInside(node, "pre"))
                text = Regex.Replace(text, @"[\t\r\n\s]+", " ");
            sb.Append(text);
            return;
        }
        if (node.NodeType != HtmlNodeType.Element) return;

        string name = node.Name.ToLowerInvariant();
        switch (name)
        {
            case "h1": AppendHeading(node, sb, ctx, 1); break;
            case "h2": AppendHeading(node, sb, ctx, 2); break;
            case "h3": AppendHeading(node, sb, ctx, 3); break;
            case "h4": AppendHeading(node, sb, ctx, 4); break;
            case "h5": AppendHeading(node, sb, ctx, 5); break;
            case "p":
                sb.Append("\n\n");
                Convert(node, sb, ctx);
                sb.Append("\n\n");
                break;
            case "br":
                sb.Append("  \n");
                break;
            case "hr":
                sb.Append("\n\n---\n\n");
                break;
            case "b":
            case "strong":
                sb.Append("**");
                Convert(node, sb, ctx);
                sb.Append("**");
                break;
            case "i":
            case "em":
                sb.Append("*");
                Convert(node, sb, ctx);
                sb.Append("*");
                break;
            case "u":
                sb.Append("<u>");
                Convert(node, sb, ctx);
                sb.Append("</u>");
                break;
            case "del":
                sb.Append("~~");
                Convert(node, sb, ctx);
                sb.Append("~~");
                break;
            case "sup":
                sb.Append("<sup>");
                Convert(node, sb, ctx);
                sb.Append("</sup>");
                break;
            case "sub":
                sb.Append("<sub>");
                Convert(node, sb, ctx);
                sb.Append("</sub>");
                break;
            case "code":
                if (IsInside(node, "pre")) Convert(node, sb, ctx);
                else { sb.Append('`'); sb.Append(node.InnerText); sb.Append('`'); }
                break;
            case "pre":
                sb.Append("\n\n```\n");
                sb.Append(node.InnerText);
                sb.Append("\n```\n\n");
                break;
            case "a":
                {
                    var text = new StringBuilder();
                    Convert(node, text, ctx);
                    var href = node.GetAttributeValue("href", "");
                    var escapedText = Regex.Replace(text.ToString(), @"([\[\]()])", @"\$1");
                    if (!string.IsNullOrEmpty(href))
                        sb.Append($"[{escapedText}]({href})");
                    else
                        sb.Append(escapedText);
                }
                break;
            case "img":
                {
                    var alt = node.GetAttributeValue("alt", "");
                    var src = node.GetAttributeValue("src", "");
                    sb.Append($"![{alt}]({src})");
                }
                break;
            case "ul":
            case "ol":
                {
                    sb.Append('\n');
                    var prevType = ctx.ListType;
                    var prevIdx = ctx.OrderedIdx;
                    ctx.ListType = name;
                    ctx.OrderedIdx = 0;
                    ctx.ListDepth++;
                    foreach (var li in node.ChildNodes.Where(c => c.Name == "li"))
                    {
                        ctx.OrderedIdx++;
                        string indent = new string(' ', (ctx.ListDepth - 1) * 2);
                        string marker = name == "ol" ? $"{ctx.OrderedIdx}. " : "- ";
                        sb.Append(indent).Append(marker);
                        var before = sb.Length;
                        Convert(li, sb, ctx);
                        // Trim trailing newlines on the list-item line
                        while (sb.Length > before && (sb[^1] == '\n' || sb[^1] == ' '))
                            sb.Length--;
                        sb.Append('\n');
                    }
                    ctx.ListDepth--;
                    ctx.ListType = prevType;
                    ctx.OrderedIdx = prevIdx;
                    sb.Append('\n');
                }
                break;
            case "li":
                Convert(node, sb, ctx);
                break;
            case "table":
                sb.Append("\n\n");
                sb.Append(node.OuterHtml);
                sb.Append("\n\n");
                break;
            case "math":
                {
                    bool block = node.GetAttributeValue("display", "") == "block";
                    if (block) sb.Append("\n$$").Append(node.InnerText.Trim()).Append("$$\n");
                    else sb.Append(" $").Append(node.InnerText.Trim()).Append("$ ");
                }
                break;
            case "chem":
                sb.Append('`').Append(node.InnerText).Append('`');
                break;
            case "div":
            case "span":
            case "small":
            case "big":
            case "caption":
            case "tbody":
            case "thead":
                Convert(node, sb, ctx);
                break;
            default:
                // Preserve unknown tags with inner convert
                Convert(node, sb, ctx);
                break;
        }
    }

    private static void AppendHeading(HtmlNode node, StringBuilder sb, Context ctx, int level)
    {
        sb.Append("\n\n").Append(new string('#', level)).Append(' ');
        Convert(node, sb, ctx);
        sb.Append("\n\n");
    }

    private static bool IsInside(HtmlNode node, params string[] tags)
    {
        var p = node.ParentNode;
        while (p != null)
        {
            if (tags.Contains(p.Name?.ToLowerInvariant())) return true;
            p = p.ParentNode;
        }
        return false;
    }

    private static string Escape(string text)
    {
        // Escape markdown special chars: *, _, $, brackets already handled in <a>
        text = text.Replace("\\", "\\\\");
        text = Regex.Replace(text, @"([*_$])", @"\$1");
        return text;
    }

    private static string PostClean(string md)
    {
        // collapse 3+ blank lines into 2
        md = Regex.Replace(md, @"\n{3,}", "\n\n");
        return md;
    }
}
