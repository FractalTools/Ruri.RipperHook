using System;
using System.Collections.Generic;
using AssetRipper.Assets;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using Ruri.Hook.Core;
using Ruri.RipperHook.Core.TypeTree;

namespace Ruri.RipperHook.Core
{
    /// <summary>
    /// The single entry point every hooked <c>ReadRelease</c> is retargeted to.
    ///
    /// A registered class is read with the game's own type tree (<see cref="TypeTreeReadPlan"/>)
    /// straight into the stock AssetRipper object. This used to go through a generated
    /// <c>Ruri.SourceGenerated</c> twin -- create a dummy of the game's layout, read it, then
    /// name-match every field back onto the real asset -- which cost a 53 MB build artifact, a
    /// reflective deep copy per asset, and a full codegen round trip for every new game version.
    /// </summary>
    public static class HookDispatcher
    {
        public delegate void ReadReleaseDelegate(object asset, ref EndianSpanReader reader);

        private static readonly object _syncRoot = new();
        private static readonly Dictionary<Type, HookInfo> _genericHookCache = new();

        private sealed class HookInfo
        {
            public ClassIDType ClassID;
            public TypeTreeVersion TargetVersion;
            public ReadReleaseDelegate? Callback;
        }

        public static void Register(Type sourceType, ClassIDType classID, TypeTreeVersion targetVersion, ReadReleaseDelegate? callback)
        {
            HookInfo hookInfo = new HookInfo
            {
                ClassID = classID,
                TargetVersion = targetVersion,
                Callback = callback
            };

            lock (_syncRoot)
            {
                _genericHookCache[sourceType] = hookInfo;
            }

            HookManager.RegisterCleanup(() =>
            {
                lock (_syncRoot)
                {
                    if (_genericHookCache.TryGetValue(sourceType, out HookInfo? current) && ReferenceEquals(current, hookInfo))
                    {
                        _genericHookCache.Remove(sourceType);
                    }
                }
            });
        }

        public static void Clear()
        {
            lock (_syncRoot)
            {
                _genericHookCache.Clear();
            }
        }

        public static void Universal_ReadRelease(object asset, ref EndianSpanReader reader)
        {
            Type type = asset.GetType();

            HookInfo hookInfo;
            lock (_syncRoot)
            {
                if (!_genericHookCache.TryGetValue(type, out hookInfo!))
                {
                    throw new InvalidOperationException($"[RipperHook] Generic hook called for unregistered type {type.FullName}");
                }
            }

            if (hookInfo.Callback != null)
            {
                hookInfo.Callback(asset, ref reader);
                return;
            }

            TypeTreeReadPlan plan = TypeTreeReadPlan.Get(hookInfo.ClassID, type, hookInfo.TargetVersion)
                ?? throw new InvalidOperationException(
                    $"[RipperHook] No type tree for {hookInfo.ClassID} at {hookInfo.TargetVersion} in {TypeTreeDatabase.Origin}.");

            plan.Read((IUnityObjectBase)asset, ref reader);
        }
    }
}
