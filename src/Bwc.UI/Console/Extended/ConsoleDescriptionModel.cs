namespace Bwc.UI.Console.Extended;

/// <summary>The two tabs under the result pane: the description pane beside the content, or the content alone.</summary>
public enum ConsoleViewTab
{
    Extended,
    Standard,
}

/// <summary>One labeled block of the description pane. The text is plain: nothing in it is parsed.</summary>
/// <param name="Id">A stable name for the section, used for automation ids.</param>
/// <param name="Label">The heading. An empty label shows the text alone (a hint).</param>
/// <param name="Text">The text. Line breaks may be CR, LF or CR LF. Empty text shows the label only.</param>
public sealed record ConsoleDescriptionSection(string Id, string Label, string Text);

/// <summary>A link of the description pane. Invoking it raises <see cref="ConsoleDescriptionPane.LinkInvoked"/>.</summary>
/// <param name="Id">A stable name for the link, used for automation ids and in the event.</param>
/// <param name="Text">The link text.</param>
public sealed record ConsoleDescriptionLink(string Id, string Text);

/// <summary>
/// What the description pane shows: a title, links, and labeled sections of plain text. The pane knows no snap-in: the
/// caller decides what the title, the sections and the links mean. Null strings and null entries are treated as empty and
/// the lists are copied, so a caller that changes its own lists afterward changes nothing here.
/// </summary>
public sealed class ConsoleDescriptionModel
{
    public ConsoleDescriptionModel(
        string? title,
        IEnumerable<ConsoleDescriptionSection?>? sections = null,
        IEnumerable<ConsoleDescriptionLink?>? links = null)
    {
        Title = title ?? string.Empty;
        Sections = (sections ?? [])
            .Where(section => section is not null)
            .Select(section => new ConsoleDescriptionSection(section!.Id ?? string.Empty, section.Label ?? string.Empty, section.Text ?? string.Empty))
            .ToList();
        Links = (links ?? [])
            .Where(link => link is not null)
            .Select(link => new ConsoleDescriptionLink(link!.Id ?? string.Empty, link.Text ?? string.Empty))
            .ToList();
    }

    public string Title { get; }

    public IReadOnlyList<ConsoleDescriptionSection> Sections { get; }

    public IReadOnlyList<ConsoleDescriptionLink> Links { get; }
}

/// <summary>The event data of a link invocation.</summary>
public sealed class ConsoleDescriptionLinkEventArgs : EventArgs
{
    public ConsoleDescriptionLinkEventArgs(ConsoleDescriptionLink link)
    {
        Link = link;
    }

    public ConsoleDescriptionLink Link { get; }
}
