using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

/// <summary>
/// Every integer the engine names that an array size might be spelled as: a define, a constant
/// declared at namespace or class scope, or a member of an enum.
///
/// An enum member is the one that bites: most are never given a value, they are simply the next
/// number, and TVC_MAX is the third member of a three-member enum. Reading only the members that
/// spell their value out left it unknown, an array sized by it one element long, and every
/// member of the View buffer after it sixteen bytes early -- a seed that hashed to nothing any
/// cook carries. So an enum is walked as the compiler walks it: from zero, or from the last
/// stated value, one up per member.
/// </summary>
public static class ConstantsCollector
{
    private static readonly Regex DefinePattern = new(
        @"^[ \t]*#define[ \t]+(?<name>[A-Za-z_][A-Za-z_0-9]*)[ \t]+(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex DeclaredPattern = new(
        @"(?:(?:static|inline|constexpr|const)\s+)+(?:int|uint|int8|uint8|int16|uint16|int32|uint32|int64|uint64|size_t|SIZE_T)\s+(?<name>[A-Za-z_][A-Za-z_0-9]*)\s*=\s*(?<value>[^;{]+);",
        RegexOptions.Compiled);

    private static readonly Regex EnumPattern = new(
        @"\benum\s+(?:class\s+|struct\s+)?(?<name>[A-Za-z_][A-Za-z_0-9]*)?\s*(?::\s*[A-Za-z_][A-Za-z_0-9:]*\s*)?\{(?<body>[^{}]*)\}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static Dictionary<string, long> Collect(IEnumerable<string> sourceFiles)
    {
        Dictionary<string, long> constants = new(StringComparer.Ordinal);
        List<(string Name, string Expression)> deferred = new();
        foreach (string file in sourceFiles)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (text.Length == 0)
            {
                continue;
            }
            bool hasDefine = text.Contains("#define ", StringComparison.Ordinal);
            bool hasDeclared = text.Contains("const", StringComparison.Ordinal);
            bool hasEnum = text.Contains("enum", StringComparison.Ordinal);
            if (!hasDefine && !hasDeclared && !hasEnum)
            {
                continue;
            }
            string stripped = UeSourceScanner.StripComments(text);
            if (hasDefine)
            {
                foreach (Match match in DefinePattern.Matches(stripped))
                {
                    Define(match.Groups["name"].Value, match.Groups["value"].Value, constants, deferred);
                }
            }
            if (hasDeclared)
            {
                foreach (Match match in DeclaredPattern.Matches(stripped))
                {
                    Define(match.Groups["name"].Value, match.Groups["value"].Value, constants, deferred);
                }
            }
            if (hasEnum)
            {
                foreach (Match match in EnumPattern.Matches(stripped))
                {
                    CollectEnum(match.Groups["body"].Value, constants);
                }
            }
        }

        // A constant spelled in terms of one declared in a file read later is not knowable on
        // the first pass; each further pass settles whatever the last one made knowable.
        for (int pass = 0; pass < 4 && deferred.Count > 0; pass++)
        {
            List<(string Name, string Expression)> still = new();
            foreach ((string name, string expression) in deferred)
            {
                if (constants.ContainsKey(name))
                {
                    continue;
                }
                if (TryEvaluate(expression, constants, out long value))
                {
                    constants[name] = value;
                }
                else
                {
                    still.Add((name, expression));
                }
            }
            deferred = still;
        }
        return constants;
    }

    private static void Define(string name, string expression, Dictionary<string, long> constants, List<(string, string)> deferred)
    {
        if (TryEvaluate(expression, constants, out long value))
        {
            constants[name] = value;
        }
        else
        {
            deferred.Add((name, expression));
        }
    }

    /// <summary>One enum body, each member valued as the compiler values it.</summary>
    private static void CollectEnum(string body, Dictionary<string, long> constants)
    {
        long next = 0;
        bool known = true;
        foreach (string entry in body.Split(','))
        {
            string member = entry.Trim();
            if (member.Length == 0 || member.StartsWith('#'))
            {
                continue;
            }
            int equals = member.IndexOf('=');
            string name = (equals >= 0 ? member[..equals] : member).Trim();
            if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z_0-9]*$"))
            {
                known = false;
                continue;
            }
            if (equals >= 0)
            {
                known = TryEvaluate(member[(equals + 1)..], constants, out long stated);
                if (known)
                {
                    next = stated;
                }
            }
            if (known)
            {
                constants.TryAdd(name, next);
                next++;
            }
        }
    }

    /// <summary>
    /// The value of an integer expression as the engine spells one for a size: literals, names
    /// already known, and the arithmetic a header does on them -- shifts, sums, products, a
    /// rounding-up division in parentheses, a cast to an integer type. Anything else is not a size.
    /// </summary>
    public static bool TryEvaluate<TValue>(string expression, IReadOnlyDictionary<string, TValue> known, out long value)
        where TValue : struct
    {
        value = 0;
        List<string> tokens = Tokens(expression);
        int position = 0;
        try
        {
            return tokens.Count > 0 && Sum(tokens, ref position, known, out value) && position == tokens.Count;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static readonly Regex TokenPattern = new(
        @"0x[0-9A-Fa-f]+[uUlL]*|\d+[uUlL]*|[A-Za-z_][A-Za-z_0-9]*(?:::[A-Za-z_][A-Za-z_0-9]*)*|<<|>>|[()+\-*/]",
        RegexOptions.Compiled);

    private static List<string> Tokens(string expression)
    {
        List<string> tokens = new();
        foreach (Match match in TokenPattern.Matches(expression))
        {
            tokens.Add(match.Value);
        }
        return tokens;
    }

    private static bool Sum<TValue>(List<string> tokens, ref int position, IReadOnlyDictionary<string, TValue> known, out long value)
        where TValue : struct
    {
        if (!Shift(tokens, ref position, known, out value))
        {
            return false;
        }
        while (position < tokens.Count && tokens[position] is "+" or "-")
        {
            string op = tokens[position++];
            if (!Shift(tokens, ref position, known, out long right))
            {
                return false;
            }
            value = op == "+" ? value + right : value - right;
        }
        return true;
    }

    private static bool Shift<TValue>(List<string> tokens, ref int position, IReadOnlyDictionary<string, TValue> known, out long value)
        where TValue : struct
    {
        if (!Product(tokens, ref position, known, out value))
        {
            return false;
        }
        while (position < tokens.Count && tokens[position] is "<<" or ">>")
        {
            string op = tokens[position++];
            if (!Product(tokens, ref position, known, out long right) || right is < 0 or > 62)
            {
                return false;
            }
            value = op == "<<" ? value << (int)right : value >> (int)right;
        }
        return true;
    }

    private static bool Product<TValue>(List<string> tokens, ref int position, IReadOnlyDictionary<string, TValue> known, out long value)
        where TValue : struct
    {
        if (!Atom(tokens, ref position, known, out value))
        {
            return false;
        }
        while (position < tokens.Count && tokens[position] is "*" or "/")
        {
            string op = tokens[position++];
            if (!Atom(tokens, ref position, known, out long right) || (op == "/" && right == 0))
            {
                return false;
            }
            value = op == "*" ? value * right : value / right;
        }
        return true;
    }

    private static readonly HashSet<string> IntegerTypeNames = new(StringComparer.Ordinal)
    {
        "int", "uint", "int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64", "size_t", "SIZE_T", "unsigned",
    };

    private static bool Atom<TValue>(List<string> tokens, ref int position, IReadOnlyDictionary<string, TValue> known, out long value)
        where TValue : struct
    {
        value = 0;
        if (position >= tokens.Count)
        {
            return false;
        }
        string token = tokens[position];
        if (token == "(")
        {
            if (position + 2 < tokens.Count && IntegerTypeNames.Contains(tokens[position + 1]) && tokens[position + 2] == ")")
            {
                position += 3;
                return Atom(tokens, ref position, known, out value);
            }
            position++;
            if (!Sum(tokens, ref position, known, out value) || position >= tokens.Count || tokens[position] != ")")
            {
                return false;
            }
            position++;
            return true;
        }
        if (token == "-")
        {
            position++;
            if (!Atom(tokens, ref position, known, out value))
            {
                return false;
            }
            value = -value;
            return true;
        }
        position++;
        if (char.IsDigit(token[0]))
        {
            string literal = token.TrimEnd('u', 'U', 'l', 'L');
            return literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.TryParse(literal.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value)
                : long.TryParse(literal, out value);
        }
        string name = token;
        int scope = name.LastIndexOf("::", StringComparison.Ordinal);
        if (scope >= 0)
        {
            name = name[(scope + 2)..];
        }
        if (known.TryGetValue(name, out TValue found))
        {
            value = Convert.ToInt64(found);
            return true;
        }
        return false;
    }
}
