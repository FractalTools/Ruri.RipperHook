using System;
using System.Collections.Generic;
using System.Threading;

namespace Ruri.Hook.Core
{
    public static class HookManager
    {
        private const string GlobalScopeId = "__global__";
        private static readonly object _syncRoot = new();
        private static readonly Dictionary<string, ScopeState> _scopes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly AsyncLocal<string?> _currentScope = new();

        private sealed class ScopeState
        {
            public List<IDisposable> Hooks { get; } = new();
            public List<Action> Cleanups { get; } = new();
        }

        public static string? CurrentScopeId => _currentScope.Value;

        public static void RunInScope(string scopeId, Action action)
        {
            if (string.IsNullOrWhiteSpace(scopeId)) throw new ArgumentException("Scope id is required.", nameof(scopeId));
            ArgumentNullException.ThrowIfNull(action);

            string? previousScope = _currentScope.Value;
            _currentScope.Value = scopeId;
            try
            {
                action();
            }
            finally
            {
                _currentScope.Value = previousScope;
            }
        }

        public static void Register(IDisposable hook)
        {
            ArgumentNullException.ThrowIfNull(hook);

            lock (_syncRoot)
            {
                GetOrCreateScopeState(GetStorageScopeId(_currentScope.Value)).Hooks.Add(hook);
            }
        }

        public static void RegisterCleanup(Action cleanup)
        {
            ArgumentNullException.ThrowIfNull(cleanup);

            lock (_syncRoot)
            {
                GetOrCreateScopeState(GetStorageScopeId(_currentScope.Value)).Cleanups.Add(cleanup);
            }
        }

        public static void DisposeScope(string scopeId)
        {
            if (string.IsNullOrWhiteSpace(scopeId))
            {
                return;
            }

            ScopeState? state;
            lock (_syncRoot)
            {
                if (!_scopes.TryGetValue(GetStorageScopeId(scopeId), out state))
                {
                    return;
                }

                _scopes.Remove(GetStorageScopeId(scopeId));
            }

            DisposeState(state);
        }

        public static void DisposeAll()
        {
            ScopeState[] states;
            lock (_syncRoot)
            {
                states = new ScopeState[_scopes.Count];
                _scopes.Values.CopyTo(states, 0);
                _scopes.Clear();
            }

            foreach (ScopeState state in states)
            {
                DisposeState(state);
            }
        }

        private static ScopeState GetOrCreateScopeState(string scopeId)
        {
            if (!_scopes.TryGetValue(scopeId, out ScopeState? state))
            {
                state = new ScopeState();
                _scopes[scopeId] = state;
            }

            return state;
        }

        private static string GetStorageScopeId(string? scopeId)
        {
            return string.IsNullOrWhiteSpace(scopeId) ? GlobalScopeId : scopeId;
        }

        private static void DisposeState(ScopeState state)
        {
            for (int i = state.Hooks.Count - 1; i >= 0; i--)
            {
                try
                {
                    state.Hooks[i].Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HookManager] Failed to dispose hook: {ex.Message}");
                }
            }

            for (int i = state.Cleanups.Count - 1; i >= 0; i--)
            {
                try
                {
                    state.Cleanups[i]();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HookManager] Failed to run hook cleanup: {ex.Message}");
                }
            }
        }
    }
}
