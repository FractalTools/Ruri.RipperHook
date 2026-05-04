using System;
using MonoMod.RuntimeDetour;
using System.Collections.Generic;

namespace Ruri.Hook.Core
{
    public static class HookManager
    {
        private static readonly List<IDisposable> _hooks = new List<IDisposable>();

        public static void Register(IDisposable hook)
        {
            _hooks.Add(hook);
        }

        public static void DisposeAll()
        {
            foreach (var hook in _hooks)
            {
                hook.Dispose();
            }
            _hooks.Clear();
        }
    }
}
