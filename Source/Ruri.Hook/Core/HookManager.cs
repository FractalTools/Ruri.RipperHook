using System;
using MonoMod.RuntimeDetour;
using System.Collections.Generic;
using System.Linq;

namespace Ruri.Hook.Core
{
    public static class HookManager
    {
        private static readonly List<IDisposable> _hooks = new List<IDisposable>();
        private static HashSet<string> _activeHookIds = new(StringComparer.OrdinalIgnoreCase);

        public static void Register(IDisposable hook)
        {
            _hooks.Add(hook);
        }

        public static IReadOnlyCollection<string> ActiveHookIds => _activeHookIds;

        public static bool HasSameHookSet(IEnumerable<string> hookIds)
        {
            HashSet<string> next = new(hookIds, StringComparer.OrdinalIgnoreCase);
            return _activeHookIds.SetEquals(next);
        }

        public static void MarkActiveHooks(IEnumerable<string> hookIds)
        {
            _activeHookIds = new HashSet<string>(hookIds, StringComparer.OrdinalIgnoreCase);
        }

        public static void DisposeAll()
        {
            foreach (var hook in _hooks)
            {
                hook.Dispose();
            }
            _hooks.Clear();
            _activeHookIds.Clear();
        }
    }
}
