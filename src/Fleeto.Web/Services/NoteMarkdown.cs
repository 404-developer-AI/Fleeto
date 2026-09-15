using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace Fleeto.Web.Services;

/// <summary>
/// Renders the markdown of a note to HTML that is safe to put in the page. A note is written by one technician and read by
/// others (and by admins), so its body is untrusted:
/// <list type="bullet">
/// <item>raw HTML is not rendered but shown as text;</item>
/// <item>images are shown as their text (the page loads nothing from elsewhere);</item>
/// <item>links are kept only for absolute http, https and mailto addresses, with rel="noopener noreferrer nofollow";
/// anything else (javascript:, data:, relative paths) is shown as text.</item>
/// </list>
/// </summary>
public static class NoteMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .UseReferralLinks("noopener", "noreferrer", "nofollow")
        .Build();

    public static MarkupString ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new MarkupString(string.Empty);
        }

        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (link.IsImage || !IsSafeUrl(link.GetDynamicUrl?.Invoke() ?? link.Url))
            {
                link.ReplaceBy(new LiteralInline(TextOf(link)));
            }
        }

        foreach (var link in document.Descendants<AutolinkInline>().ToList())
        {
            if (!(link.IsEmail || IsSafeUrl(link.Url)))
            {
                link.ReplaceBy(new LiteralInline(link.Url));
            }
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return new MarkupString(writer.ToString());
    }

    internal static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto;
    }

    private static string TextOf(ContainerInline container)
    {
        var text = new StringBuilder();
        foreach (var inline in container.Descendants<LiteralInline>())
        {
            text.Append(inline.Content.ToString());
        }

        return text.ToString();
    }
}
