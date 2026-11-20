namespace Ruri.RipperHook.RuriEngine;

internal static class ScriptIdentity
{
    public static bool TryResolve(EngineTypes types, string @namespace, string className, [NotNullWhen(true)] out EngineClass? engineClass, out string reason)
    {
        engineClass = null;
        reason = string.Empty;
        className = className.Replace('/', '+');
        if (className.Length == 0)
        {
            reason = "script has no class name";
            return false;
        }
        if (@namespace.Length > 0 && types.TryGet(@namespace + "." + className, out engineClass))
        {
            return true;
        }
        IReadOnlyList<string> candidates = types.FullNamesOfSimpleName(className);
        if (candidates.Count == 1 && types.TryGet(candidates[0], out engineClass))
        {
            return true;
        }
        reason = candidates.Count == 0
            ? $"{className}: no engine class of that name"
            : $"{className}: {candidates.Count} engine classes of that name";
        return false;
    }
}
