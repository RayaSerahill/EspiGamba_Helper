using System.Text.RegularExpressions;

namespace EspiGambaHelper.Core;

public sealed record DiceRoll(string Name, bool IsSelf, int Result, int Maximum)
{
    // /dice uses Addon 3567/3568: identity is in the chat sender, not the message body.
    public bool UsesChatSender => Name.Length == 0;
}

public static partial class DiceParser
{
    // English LogMessage rows 856 and 3887. Icon payloads are not part of TextValue.
    [GeneratedRegex(@"^Random!\s+(?<name>.+?)\s+rolls? a\s*(?<result>[0-9]+)(?:\s+\(out of (?<maximum>[0-9]+)\))?\.$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    // Addon rows 3567/3568, with the auto-translate and dice icon payloads removed.
    [GeneratedRegex(@"^Random!\s*(?:\(1-(?<maximum>[0-9]+)\)\s*)?(?<result>[0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChatDicePattern();

    public static DiceRoll? Parse(string text)
    {
        var match = ChatDicePattern().Match(text.Trim());
        if (!match.Success) match = Pattern().Match(text.Trim());
        if (!match.Success || !int.TryParse(match.Groups["result"].Value, out var result))
            return null;
        var maximum = 999;
        if (match.Groups["maximum"].Success && !int.TryParse(match.Groups["maximum"].Value, out maximum))
            return null;
        if (maximum is < 2 or > 999 || result < 0 || result > maximum)
            return null;
        var name = match.Groups["name"].Value.Trim();
        return new DiceRoll(name, name == "You", result, maximum);
    }
}
