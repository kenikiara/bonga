using System.Text.RegularExpressions;

namespace VoiceFlow.Core;

/// <summary>
/// The "AI edits" pipeline: cleans raw transcription into polished text.
/// Order: snippets -> voice commands -> filler removal -> replacements ->
/// duplicate-word collapse -> whitespace/capitalization/punctuation fixes.
/// </summary>
public static class TextFormatter
{
    private static readonly string[] Fillers =
        { "um", "umm", "uh", "uhh", "uhm", "erm", "hmm", "mhm" };

    public static string Apply(string raw, AppSettings s)
    {
        string text = (raw ?? "").Trim();
        if (text.Length == 0) return "";

        // 1. Snippets: whole-utterance trigger match ("insert my email")
        string bare = StripPunctuation(text).ToLowerInvariant();
        foreach (var snip in s.Snippets)
        {
            if (string.IsNullOrWhiteSpace(snip.Trigger)) continue;
            if (bare == StripPunctuation(snip.Trigger).ToLowerInvariant())
                return snip.Expansion;
        }

        // 2. Spoken commands
        if (s.VoiceCommands)
        {
            text = Regex.Replace(text, @"[,.]?\s*\bnew paragraph\b[,.]?\s*", "\n\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"[,.]?\s*\bnew line\b[,.]?\s*", "\n", RegexOptions.IgnoreCase);
        }

        // 3. Filler words
        if (s.RemoveFillers)
        {
            foreach (var f in Fillers)
                text = Regex.Replace(text, $@"(^|\s)\b{f}\b[,.]?(?=\s|$)", "$1", RegexOptions.IgnoreCase);
        }

        // 4. Personal dictionary hard replacements (whole word, keep-case target)
        foreach (var rep in s.Replacements)
        {
            if (string.IsNullOrWhiteSpace(rep.From)) continue;
            text = Regex.Replace(text, $@"\b{Regex.Escape(rep.From)}\b", rep.To.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }

        // 5. Collapse immediately repeated words ("the the" -> "the")
        text = Regex.Replace(text, @"\b(\w+)(\s+\1)+\b", "$1", RegexOptions.IgnoreCase);

        // 6. Whitespace normalization (preserve intentional newlines)
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @" ?\n ?", "\n");
        text = Regex.Replace(text, @"\s+([,.!?;:])", "$1");
        text = text.Trim();
        if (text.Length == 0) return "";

        // 7. Capitalization: first letter of each sentence
        if (s.AutoCapitalize)
            text = CapitalizeSentences(text);

        // 8. Ensure terminal punctuation for sentence-like output
        if (s.AutoPunctuate && text.Length > 12 && char.IsLetterOrDigit(text[^1]))
            text += ".";

        return text;
    }

    private static string StripPunctuation(string input) =>
        Regex.Replace(input, @"[^\w\s]", "").Trim();

    private static string CapitalizeSentences(string text)
    {
        var chars = text.ToCharArray();
        bool atSentenceStart = true;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (atSentenceStart && char.IsLetter(c))
            {
                chars[i] = char.ToUpper(c);
                atSentenceStart = false;
            }
            else if (c == '.' || c == '!' || c == '?' || c == '\n')
            {
                atSentenceStart = true;
            }
            else if (!char.IsWhiteSpace(c))
            {
                atSentenceStart = false;
            }
        }
        return new string(chars);
    }
}
