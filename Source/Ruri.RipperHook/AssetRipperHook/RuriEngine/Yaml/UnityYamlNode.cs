namespace Ruri.RipperHook.RuriEngine;

internal abstract class UnityYamlNode
{
}

internal sealed class UnityYamlScalar(string text, bool quoted) : UnityYamlNode
{
    public string Text { get; } = text;

    public bool Quoted { get; } = quoted;

    public bool IsEmpty => !Quoted && Text.Length == 0;
}

internal sealed class UnityYamlSequence : UnityYamlNode
{
    public List<UnityYamlNode> Items { get; } = new();
}

internal sealed class UnityYamlMapping : UnityYamlNode
{
    public List<KeyValuePair<string, UnityYamlNode>> Entries { get; } = new();

    public void Add(string key, UnityYamlNode value) => Entries.Add(new KeyValuePair<string, UnityYamlNode>(key, value));

    public bool TryGet(string key, [NotNullWhen(true)] out UnityYamlNode? value)
    {
        foreach (KeyValuePair<string, UnityYamlNode> entry in Entries)
        {
            if (entry.Key == key)
            {
                value = entry.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    public string? TryGetText(string key)
    {
        return TryGet(key, out UnityYamlNode? node) && node is UnityYamlScalar scalar ? scalar.Text : null;
    }
}

internal readonly record struct UnityYamlDocument(int ClassId, long FileId, bool Stripped, UnityYamlNode Root);
