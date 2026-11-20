using System.Globalization;
using System.Text;

namespace Ruri.RipperHook.RuriEngine;

internal static class UnityYamlParser
{
    private const string DocumentStart = "---";
    private const string ClassTagPrefix = "!u!";
    private const string StrippedMarker = "stripped";

    public static List<UnityYamlDocument> ParseDocuments(string text)
    {
        List<UnityYamlDocument> documents = new();
        List<string> body = new();
        bool inDocument = false;
        int classId = -1;
        long fileId = 0;
        bool stripped = false;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith(DocumentStart, StringComparison.Ordinal))
            {
                Flush();
                (classId, fileId, stripped) = ParseHeader(line);
                inDocument = true;
                continue;
            }
            if (!inDocument)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] is '%' or '#')
                {
                    continue;
                }
                inDocument = true;
            }
            body.Add(line);
        }
        Flush();
        return documents;

        void Flush()
        {
            if (inDocument)
            {
                documents.Add(new UnityYamlDocument(classId, fileId, stripped, new Reader(body.ToArray()).ParseRoot()));
            }
            body.Clear();
            classId = -1;
            fileId = 0;
            stripped = false;
        }
    }

    public static UnityYamlNode ParseSingle(string text)
    {
        List<UnityYamlDocument> documents = ParseDocuments(text);
        return documents.Count == 0 ? new UnityYamlMapping() : documents[0].Root;
    }

    private static (int ClassId, long FileId, bool Stripped) ParseHeader(string line)
    {
        int classId = -1;
        long fileId = 0;
        bool stripped = false;
        foreach (string token in line[DocumentStart.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(ClassTagPrefix, StringComparison.Ordinal))
            {
                classId = int.Parse(token[ClassTagPrefix.Length..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }
            else if (token[0] == '&')
            {
                fileId = long.Parse(token[1..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }
            else if (token == StrippedMarker)
            {
                stripped = true;
            }
        }
        return (classId, fileId, stripped);
    }

    private readonly record struct Line(int Indent, string Text);

    private sealed class Reader(string[] lines)
    {
        private int position;

        public UnityYamlNode ParseRoot()
        {
            return TryPeek(out Line line) ? ParseBlock(line.Indent) : new UnityYamlMapping();
        }

        private bool TryPeek(out Line line)
        {
            while (position < lines.Length)
            {
                string raw = lines[position];
                int indent = 0;
                while (indent < raw.Length && raw[indent] == ' ')
                {
                    indent++;
                }
                if (indent == raw.Length || raw[indent] == '#')
                {
                    position++;
                    continue;
                }
                line = new Line(indent, raw[indent..].TrimEnd());
                return true;
            }
            line = default;
            return false;
        }

        private UnityYamlNode ParseBlock(int indent)
        {
            TryPeek(out Line line);
            if (IsSequenceItem(line.Text))
            {
                return ParseSequence(indent);
            }
            if (TrySplitEntry(line.Text, out _, out _))
            {
                return ParseMapping(indent);
            }
            position++;
            return ParsePlain(line.Text, indent - 1);
        }

        private UnityYamlMapping ParseMapping(int indent)
        {
            UnityYamlMapping mapping = new();
            while (TryPeek(out Line line) && line.Indent == indent && !IsSequenceItem(line.Text))
            {
                if (!TrySplitEntry(line.Text, out string key, out string rest))
                {
                    throw Error($"expected 'key: value', found '{line.Text}'");
                }
                position++;
                mapping.Add(key, ParseValue(rest, indent, ownerIsKey: true));
            }
            if (TryPeek(out Line stop) && stop.Indent > indent)
            {
                throw Error($"unexpected indentation before '{stop.Text}'");
            }
            return mapping;
        }

        private UnityYamlSequence ParseSequence(int indent)
        {
            UnityYamlSequence sequence = new();
            while (TryPeek(out Line line) && line.Indent == indent && IsSequenceItem(line.Text))
            {
                string rest = line.Text.Length == 1 ? string.Empty : line.Text[1..].TrimStart();
                int offset = indent + (line.Text.Length - rest.Length);
                if (rest.Length == 0)
                {
                    position++;
                    sequence.Items.Add(ParseValue(string.Empty, indent, ownerIsKey: false));
                }
                else if (IsSequenceItem(rest))
                {
                    lines[position] = new string(' ', offset) + rest;
                    sequence.Items.Add(ParseSequence(offset));
                }
                else if (TrySplitEntry(rest, out _, out _))
                {
                    lines[position] = new string(' ', offset) + rest;
                    sequence.Items.Add(ParseMapping(offset));
                }
                else
                {
                    position++;
                    sequence.Items.Add(ParseValue(rest, indent, ownerIsKey: false));
                }
            }
            return sequence;
        }

        private UnityYamlNode ParseValue(string rest, int ownerIndent, bool ownerIsKey)
        {
            if (rest.Length > 0 && rest[0] == '#')
            {
                rest = string.Empty;
            }
            if (rest.Length == 0)
            {
                if (!TryPeek(out Line next))
                {
                    return new UnityYamlScalar(string.Empty, false);
                }
                if (next.Indent > ownerIndent)
                {
                    return ParseBlock(next.Indent);
                }
                if (ownerIsKey && next.Indent == ownerIndent && IsSequenceItem(next.Text))
                {
                    return ParseSequence(ownerIndent);
                }
                return new UnityYamlScalar(string.Empty, false);
            }
            switch (rest[0])
            {
                case '{':
                case '[':
                    return ParseFlow(rest);
                case '\'':
                case '"':
                    return ParseQuoted(rest);
                case '|':
                case '>':
                    throw Error("block scalars are not part of the Unity YAML subset");
                default:
                    return ParsePlain(rest, ownerIndent);
            }
        }

        private UnityYamlScalar ParsePlain(string rest, int ownerIndent)
        {
            StringBuilder text = new(StripComment(rest));
            while (TryPeek(out Line next) && next.Indent > ownerIndent)
            {
                position++;
                text.Append(' ').Append(StripComment(next.Text));
            }
            return new UnityYamlScalar(text.ToString().TrimEnd(), false);
        }

        private UnityYamlNode ParseFlow(string rest)
        {
            StringBuilder text = new(rest);
            while (!IsBalanced(text.ToString()))
            {
                if (position >= lines.Length)
                {
                    throw Error("unterminated flow collection");
                }
                text.Append(' ').Append(lines[position++].Trim());
            }
            return new FlowReader(text.ToString(), this).ReadValue();
        }

        private UnityYamlScalar ParseQuoted(string rest)
        {
            string current = rest;
            return ReadQuoted(ref current, 0, out _);
        }

        internal UnityYamlScalar ReadQuoted(ref string current, int start, out int end)
        {
            char quote = current[start];
            StringBuilder value = new();
            int index = start + 1;
            while (true)
            {
                bool continuation = false;
                while (index < current.Length)
                {
                    char c = current[index];
                    if (quote == '\'')
                    {
                        if (c == '\'')
                        {
                            if (index + 1 < current.Length && current[index + 1] == '\'')
                            {
                                value.Append('\'');
                                index += 2;
                                continue;
                            }
                            end = index + 1;
                            return new UnityYamlScalar(value.ToString(), true);
                        }
                        value.Append(c);
                        index++;
                    }
                    else if (c == '"')
                    {
                        end = index + 1;
                        return new UnityYamlScalar(value.ToString(), true);
                    }
                    else if (c == '\\')
                    {
                        index = ReadEscape(current, index, value, out continuation);
                    }
                    else
                    {
                        value.Append(c);
                        index++;
                    }
                }

                int blankLines = 0;
                while (true)
                {
                    if (position >= lines.Length)
                    {
                        throw Error("unterminated quoted scalar");
                    }
                    string next = lines[position++].Trim();
                    if (next.Length == 0)
                    {
                        blankLines++;
                        continue;
                    }
                    current = next;
                    break;
                }
                if (blankLines > 0)
                {
                    value.Append('\n', blankLines);
                }
                else if (!continuation)
                {
                    value.Append(' ');
                }
                index = 0;
            }
        }

        private static int ReadEscape(string text, int index, StringBuilder value, out bool continuation)
        {
            continuation = false;
            if (index + 1 >= text.Length)
            {
                continuation = true;
                return index + 1;
            }
            char escape = text[index + 1];
            index += 2;
            switch (escape)
            {
                case '0': value.Append('\0'); break;
                case 'a': value.Append('\a'); break;
                case 'b': value.Append('\b'); break;
                case 't': case '\t': value.Append('\t'); break;
                case 'n': value.Append('\n'); break;
                case 'v': value.Append('\v'); break;
                case 'f': value.Append('\f'); break;
                case 'r': value.Append('\r'); break;
                case 'e': value.Append('\u001b'); break;
                case 'N': value.Append('\u0085'); break;
                case '_': value.Append('\u00a0'); break;
                case 'L': value.Append('\u2028'); break;
                case 'P': value.Append('\u2029'); break;
                case 'x': index = AppendCodePoint(text, index, 2, value); break;
                case 'u': index = AppendCodePoint(text, index, 4, value); break;
                case 'U': index = AppendCodePoint(text, index, 8, value); break;
                default: value.Append(escape); break;
            }
            return index;
        }

        private static int AppendCodePoint(string text, int index, int digits, StringBuilder value)
        {
            int codePoint = int.Parse(text.AsSpan(index, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            value.Append(char.ConvertFromUtf32(codePoint));
            return index + digits;
        }

        private static bool IsSequenceItem(string text) => text == "-" || text.StartsWith("- ", StringComparison.Ordinal);

        private static bool TrySplitEntry(string text, out string key, out string rest)
        {
            key = string.Empty;
            rest = string.Empty;
            if (text.Length == 0 || text[0] is '{' or '[')
            {
                return false;
            }
            int separator = -1;
            if (text[0] is '\'' or '"')
            {
                char quote = text[0];
                int close = 1;
                while (close < text.Length)
                {
                    if (text[close] == quote && !(quote == '\'' && close + 1 < text.Length && text[close + 1] == '\''))
                    {
                        break;
                    }
                    close += quote == '\'' && text[close] == '\'' ? 2 : 1;
                }
                int colon = close + 1;
                while (colon < text.Length && text[colon] == ' ')
                {
                    colon++;
                }
                if (close >= text.Length || colon >= text.Length || text[colon] != ':')
                {
                    return false;
                }
                key = text[1..close];
                separator = colon;
            }
            else
            {
                for (int index = 0; index < text.Length; index++)
                {
                    if (text[index] == ':' && (index + 1 == text.Length || text[index + 1] == ' '))
                    {
                        key = text[..index].TrimEnd();
                        separator = index;
                        break;
                    }
                }
                if (separator < 0)
                {
                    return false;
                }
            }
            rest = text[(separator + 1)..].TrimStart();
            return true;
        }

        private static string StripComment(string text)
        {
            int comment = text.IndexOf(" #", StringComparison.Ordinal);
            return comment < 0 ? text : text[..comment];
        }

        private static bool IsBalanced(string text)
        {
            int depth = 0;
            char quote = '\0';
            for (int index = 0; index < text.Length; index++)
            {
                char c = text[index];
                if (quote != '\0')
                {
                    if (quote == '"' && c == '\\')
                    {
                        index++;
                    }
                    else if (c == quote)
                    {
                        if (quote == '\'' && index + 1 < text.Length && text[index + 1] == '\'')
                        {
                            index++;
                        }
                        else
                        {
                            quote = '\0';
                        }
                    }
                    continue;
                }
                switch (c)
                {
                    case '\'':
                    case '"':
                        quote = c;
                        break;
                    case '{':
                    case '[':
                        depth++;
                        break;
                    case '}':
                    case ']':
                        depth--;
                        break;
                }
            }
            return depth <= 0 && quote == '\0';
        }

        private FormatException Error(string message) => new($"Unity YAML line {position + 1}: {message}");
    }

    private sealed class FlowReader(string text, Reader reader)
    {
        private int index;

        public UnityYamlNode ReadValue()
        {
            SkipSpaces();
            if (index >= text.Length)
            {
                return new UnityYamlScalar(string.Empty, false);
            }
            return text[index] switch
            {
                '{' => ReadMapping(),
                '[' => ReadSequence(),
                '\'' or '"' => ReadQuotedScalar(),
                _ => ReadPlain(),
            };
        }

        private UnityYamlMapping ReadMapping()
        {
            UnityYamlMapping mapping = new();
            index++;
            while (true)
            {
                SkipSpaces();
                if (index >= text.Length)
                {
                    throw new FormatException($"Unity YAML flow mapping is not closed: {text}");
                }
                if (text[index] == '}')
                {
                    index++;
                    return mapping;
                }
                string key = ReadKey();
                SkipSpaces();
                if (index < text.Length && text[index] == ':')
                {
                    index++;
                }
                SkipSpaces();
                UnityYamlNode value = index < text.Length && text[index] is ',' or '}'
                    ? new UnityYamlScalar(string.Empty, false)
                    : ReadValue();
                mapping.Add(key, value);
                SkipSpaces();
                if (index < text.Length && text[index] == ',')
                {
                    index++;
                }
            }
        }

        private UnityYamlSequence ReadSequence()
        {
            UnityYamlSequence sequence = new();
            index++;
            while (true)
            {
                SkipSpaces();
                if (index >= text.Length)
                {
                    throw new FormatException($"Unity YAML flow sequence is not closed: {text}");
                }
                if (text[index] == ']')
                {
                    index++;
                    return sequence;
                }
                sequence.Items.Add(ReadValue());
                SkipSpaces();
                if (index < text.Length && text[index] == ',')
                {
                    index++;
                }
            }
        }

        private string ReadKey()
        {
            if (index < text.Length && text[index] is '\'' or '"')
            {
                return ReadQuotedScalar().Text;
            }
            int start = index;
            while (index < text.Length)
            {
                char c = text[index];
                if (c == ':' && (index + 1 >= text.Length || text[index + 1] is ' ' or ',' or '}' or ']'))
                {
                    break;
                }
                if (c is ',' or '}' or ']')
                {
                    break;
                }
                index++;
            }
            return text[start..index].Trim();
        }

        private UnityYamlScalar ReadQuotedScalar()
        {
            string current = text;
            UnityYamlScalar scalar = reader.ReadQuoted(ref current, index, out int end);
            index = end;
            return scalar;
        }

        private UnityYamlScalar ReadPlain()
        {
            int start = index;
            while (index < text.Length && text[index] is not (',' or '}' or ']'))
            {
                index++;
            }
            return new UnityYamlScalar(text[start..index].Trim(), false);
        }

        private void SkipSpaces()
        {
            while (index < text.Length && text[index] == ' ')
            {
                index++;
            }
        }
    }
}
