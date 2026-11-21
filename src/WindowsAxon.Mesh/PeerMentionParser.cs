namespace WindowsAxon.Mesh;

public static class PeerMentionParser
{
    private static readonly char[] Separators = [' ', '\t', '\r', '\n', ',', ':', ';'];

    /// <summary>
    /// Parses a leading @peer mention. Matches known peer names longest-first (supporting spaces, e.g. "@computer B, ..."),
    /// and falls back to the first token after '@' so offline peers produce an explicit error.
    /// </summary>
    public static bool TryParse(string input,
                                IReadOnlyCollection<string> knownPeers,
                                out string targetNode,
                                out string prompt)
    {
        targetNode = "";
        prompt = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.TrimStart();
        if (!trimmed.StartsWith('@') || trimmed.Length < 2 || char.IsWhiteSpace(trimmed[1]))
        {
            return false;
        }

        var afterAt = trimmed[1..];
        foreach (var peer in knownPeers.Where(p => !string.IsNullOrWhiteSpace(p))
                     .OrderByDescending(p => p.Length))
        {
            if (afterAt.StartsWith(peer, StringComparison.OrdinalIgnoreCase)
                && (afterAt.Length == peer.Length || IsBoundary(afterAt[peer.Length])))
            {
                targetNode = peer;
                prompt = CleanPrompt(afterAt[peer.Length..]);
                return true;
            }
        }

        var end = afterAt.IndexOfAny(Separators);
        if (end < 0)
        {
            targetNode = afterAt;
            prompt = "";
            return true;
        }

        targetNode = afterAt[..end];
        prompt = CleanPrompt(afterAt[end..]);
        return targetNode.Length > 0;
    }

    private static bool IsBoundary(char ch) => Separators.Contains(ch);

    private static string CleanPrompt(string remainder) =>
        remainder.TrimStart(' ', '\t', '\r', '\n', ',', ':', ';').TrimEnd();
}

