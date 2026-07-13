using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

using Ruri.Hook.Core;

namespace Ruri.Hook.Utils
{
    public static class ReflectionExtensions
    {
        #region Generic Judgment

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BindingFlags AnyBindFlag()
        {
            // FlattenHierarchy is required for a derived type's GetMethods(explicitFlags) to include
            // a base class's *static* members (instance members are always included regardless of
            // this flag). Without it, a hook class that inherits a shared static [RetargetMethod]
            // from a common base never has that method discovered by Registry.ApplyTypeHooks(GetType()).
            return BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BindingFlags PublicInstanceBindFlag()
        {
            return BindingFlags.Public | BindingFlags.Instance;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BindingFlags PrivateInstanceBindFlag()
        {
            return BindingFlags.NonPublic | BindingFlags.Instance;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BindingFlags PublicStaticBindFlag()
        {
            return BindingFlags.Public | BindingFlags.Static;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BindingFlags PrivateStaticBindFlag()
        {
            return BindingFlags.NonPublic | BindingFlags.Static;
        }

        #endregion

        #region Method Reflection

        // Guards against the same original method being IL-hooked twice within one hook activation --
        // e.g. an own-declared method that Registry.ApplyTypeHooks(GetType()) already found being
        // re-submitted via AddMethodHook, or (after the FlattenHierarchy fix above) an inherited
        // static surfacing from two different scan entry points. Policy is LAST WINS: a repeat
        // registration disposes the previously-installed hook and installs its own, rather than being
        // silently dropped. This matters beyond "identical duplicate is wasteful" -- RuriHook.
        // InitAttributeHook runs Registry.ApplyTypeHooks(GetType()) (auto-discovery, including
        // inherited statics after the flag fix) BEFORE processing the hand-curated AddMethodHook list,
        // so when a version's explicit list intentionally supersedes something auto-discovery also
        // finds (a newer override of an inherited base hook, the exact EndField TagIDToName /
        // Safe_TagIDToName shape), the deliberate, later registration must be the one left standing.
        // Cleared per hook activation, mirroring HookManager's own scope lifecycle.
        private static readonly Dictionary<MethodBase, IDisposable> _funcHookedSources = new();
        private static readonly Dictionary<MethodBase, IDisposable> _ctorFuncHookedSources = new();
        private static readonly Dictionary<(MethodBase Source, bool IsBefore, bool IsReturn), IDisposable> _callHookedSources = new();

        public static void ClearAppliedHookGuards()
        {
            _funcHookedSources.Clear();
            _ctorFuncHookedSources.Clear();
            _callHookedSources.Clear();
        }

        private static void SupersedePrevious<TKey>(Dictionary<TKey, IDisposable> registered, TKey key, string srcDescription)
        {
            if (registered.Remove(key, out IDisposable? previous))
            {
                HookLogger.LogRaw($"    [!] {srcDescription} already hooked this activation -- superseding the earlier hook");
                previous.Dispose();
            }
        }

        public static void RetargetCallFunc(Func<ILContext, bool> func, MethodInfo srcMethod)
        {
            SupersedePrevious(_funcHookedSources, srcMethod, $"{srcMethod.DeclaringType?.Name}.{srcMethod.Name}");

            var hookDest = new ILContext.Manipulator(il =>
            {
                if (!func(il))
                    throw new Exception($"Hook {srcMethod.DeclaringType.Name}.{srcMethod.Name} Fail");
            });
            var hook = new ILHook(srcMethod, hookDest);
            HookManager.Register(hook);
            _funcHookedSources[srcMethod] = hook;
            HookManager.RegisterCleanup(() => _funcHookedSources.Remove(srcMethod));
            HookLogger.LogSuccessRaw($"    [+] Hooked {srcMethod.DeclaringType?.Name}.{srcMethod.Name} -> {func.Method.Name}");
        }

        public static void RetargetCallCtorFunc(Func<ILContext, bool> func, ConstructorInfo srcMethod)
        {
            SupersedePrevious(_ctorFuncHookedSources, srcMethod, $"{srcMethod.DeclaringType?.Name}.{srcMethod.Name}");

            var hookDest = new ILContext.Manipulator(il =>
            {
                if (!func(il))
                    throw new Exception($"Hook {srcMethod.DeclaringType.Name}.{srcMethod.Name} Fail");
            });
            var hook = new ILHook(srcMethod, hookDest);
            HookManager.Register(hook);
            _ctorFuncHookedSources[srcMethod] = hook;
            HookManager.RegisterCleanup(() => _ctorFuncHookedSources.Remove(srcMethod));
            HookLogger.LogSuccessRaw($"    [+] Hooked {srcMethod.DeclaringType?.Name}.{srcMethod.Name} -> {func.Method.Name}");
        }

        /// <summary>
        /// Default behavior mimics prefix injection and return (replace original).
        /// isBefore chooses injection point (Start vs End/Before Ret).
        /// isReturn chooses whether to return immediately or continue.
        /// </summary>
        public static void RetargetCall(MethodInfo srcMethod, MethodInfo targetMethod, int maxArgIndex = 1, bool isBefore = true, bool isReturn = true)
        {
            var callKey = (srcMethod, isBefore, isReturn);
            SupersedePrevious(_callHookedSources, callKey, $"{srcMethod.DeclaringType?.Name}.{srcMethod.Name}");

            var hookDest = new ILContext.Manipulator(il =>
            {
                var ilCursor = new ILCursor(il);
                Action inject = () =>
                {
                    for (var i = 0; i <= maxArgIndex; i++)
                    {
                        switch (i)
                        {
                            case 0:
                                ilCursor.Emit(OpCodes.Ldarg_0);
                                continue;
                            case 1:
                                ilCursor.Emit(OpCodes.Ldarg_1);
                                continue;
                            case 2:
                                ilCursor.Emit(OpCodes.Ldarg_2);
                                continue;
                            case 3:
                                ilCursor.Emit(OpCodes.Ldarg_3);
                                continue;
                            default:
                                ilCursor.Emit(OpCodes.Ldarg, i);
                                continue;
                        }
                    }
                    ilCursor.Emit(OpCodes.Call, targetMethod);
                    if (isReturn)
                    {
                        ilCursor.Emit(OpCodes.Ret);
                    }
                    ilCursor.SearchTarget = SearchTarget.Next; 
                };

                if (!isBefore) 
                    while (ilCursor.TryGotoNext(MoveType.Before, instr => instr.OpCode == OpCodes.Ret))
                        inject();
                else
                    inject();
            });

            var callHook = new ILHook(srcMethod, hookDest);
            HookManager.Register(callHook);
            _callHookedSources[callKey] = callHook;
            HookManager.RegisterCleanup(() => _callHookedSources.Remove(callKey));

            if (targetMethod.Name != "Universal_ReadRelease")
            {
                HookLogger.LogSuccessRaw($"    [+] Hooked {srcMethod.DeclaringType?.Name}.{srcMethod.Name} -> {targetMethod.Name}");
            }
        }

        #endregion

        #region Deep Duck Copy

        private static readonly ConcurrentDictionary<Type, FieldInfo[]> _fieldCache = new();

        private class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
            public static readonly ReferenceEqualityComparer Instance = new();
        }

        public static void ClassDeepCopy(object src, object dst)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (dst == null) throw new ArgumentNullException(nameof(dst));

            var context = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
            context[src] = dst;

            DeepCopyFields(src, dst, context);
        }

        private static object DeepCopy(object srcObj, Type targetType, Dictionary<object, object> context)
        {
            if (srcObj == null) return null;

            if (context.TryGetValue(srcObj, out var existingDst))
                return existingDst;

            var srcType = srcObj.GetType();

            if (targetType.IsAssignableFrom(srcType) && srcType.IsValueType)
                return srcObj; 
            if (srcType == typeof(string))
                return srcObj; 

            if (srcType.IsArray && targetType.IsArray)
            {
                var srcArray = (Array)srcObj;
                var elementType = targetType.GetElementType()!;
                var length = srcArray.Length;
                var dstArray = Array.CreateInstance(elementType, length);

                context[srcObj] = dstArray; 

                for (var i = 0; i < length; i++)
                {
                    var srcVal = srcArray.GetValue(i);
                    var dstVal = DeepCopy(srcVal, elementType, context);
                    dstArray.SetValue(dstVal, i);
                }
                return dstArray;
            }

            if (typeof(IList).IsAssignableFrom(srcType) && typeof(IList).IsAssignableFrom(targetType))
            {
                var dstList = (IList)CreateInstance(targetType);
                context[srcObj] = dstList;

                var srcList = (IList)srcObj;

                var dstItemType = targetType.IsGenericType
                    ? targetType.GetGenericArguments()[0]
                    : typeof(object);

                foreach (var item in srcList)
                {
                    var convertedItem = DeepCopy(item, dstItemType, context);
                    dstList.Add(convertedItem);
                }
                return dstList;
            }

            if (typeof(IDictionary).IsAssignableFrom(srcType) && typeof(IDictionary).IsAssignableFrom(targetType))
            {
                var dstDict = (IDictionary)CreateInstance(targetType);
                context[srcObj] = dstDict;

                var srcDict = (IDictionary)srcObj;

                var genericArgs = targetType.IsGenericType ? targetType.GetGenericArguments() : new[] { typeof(object), typeof(object) };
                var keyType = genericArgs[0];
                var valueType = genericArgs[1];

                foreach (DictionaryEntry entry in srcDict)
                {
                    var newKey = DeepCopy(entry.Key, keyType, context);
                    var newValue = DeepCopy(entry.Value, valueType, context);
                    dstDict.Add(newKey, newValue);
                }
                return dstDict;
            }

            var newDstObj = CreateInstance(targetType);
            context[srcObj] = newDstObj; 

            DeepCopyFields(srcObj, newDstObj, context);

            return newDstObj;
        }

        private static void DeepCopyFields(object src, object dst, Dictionary<object, object> context)
        {
            var srcType = src.GetType();
            var dstType = dst.GetType();

            var srcFields = GetCachedFields(srcType);
            var dstFields = GetCachedFields(dstType);

            var dstMap = new Dictionary<string, FieldInfo>();
            foreach (var f in dstFields)
                dstMap[f.Name] = f;

            foreach (var srcField in srcFields)
            {
                if (!dstMap.TryGetValue(srcField.Name, out var dstField))
                    continue;

                var srcValue = srcField.GetValue(src);
                var dstValue = DeepCopy(srcValue, dstField.FieldType, context);

                try
                {
                    dstField.SetValue(dst, dstValue);
                }
                catch (Exception)
                {
                }
            }
        }

        private static FieldInfo[] GetCachedFields(Type type)
        {
            return _fieldCache.GetOrAdd(type, t =>
            {
                var fields = new List<FieldInfo>();
                while (t != null && t != typeof(object))
                {
                    fields.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                    t = t.BaseType;
                }
                return fields.ToArray();
            });
        }

        private static object CreateInstance(Type type)
        {
            try
            {
                return Activator.CreateInstance(type, true);
            }
            catch
            {
                try
                {
                    return FormatterServices.GetUninitializedObject(type);
                }
                catch
                {
                    return null;
                }
            }
        }

        #endregion
    }
}
