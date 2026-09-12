using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Processing;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Ruri.Hook.Attributes;
using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.HookUtils.ExportHandlerHook;

public class ExportHandlerHook : CommonHook, IHookModule
{
    public delegate IEnumerable<IAssetProcessor> AssetProcessorDelegate(FullConfiguration settings);

    private static readonly List<AssetProcessorRegistration> Registrations = new();

    private static readonly PropertyInfo HandlerSettings = typeof(ExportHandler)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single(property => property.PropertyType == typeof(FullConfiguration));

    public void OnApply()
    {
        Registry.ApplyTypeHooks(GetType());
    }

    public static void Register(AssetProcessorRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (Registrations.Any(existing => existing.Factory.Equals(registration.Factory)))
        {
            return;
        }
        Registrations.Add(registration);
    }

    [RetargetMethodFunc(typeof(ExportHandler))]
    public static bool ExportHandler_GetProcessors(ILContext il)
    {
        ILCursor cursor = new(il);

        int injected = 0;
        while (cursor.TryGotoNext(MoveType.Before, instruction => instruction.OpCode == OpCodes.Ret))
        {
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.EmitDelegate<Func<IEnumerable<IAssetProcessor>, ExportHandler, IEnumerable<IAssetProcessor>>>(Splice);
            cursor.Index++;
            injected++;
        }

        return injected > 0;
    }

    private static IEnumerable<IAssetProcessor> Splice(IEnumerable<IAssetProcessor> pipeline, ExportHandler handler)
    {
        FullConfiguration settings = (FullConfiguration)HandlerSettings.GetValue(handler)!;
        List<IAssetProcessor> processors = new(pipeline);
        foreach (AssetProcessorRegistration registration in Registrations)
        {
            int anchor = processors.FindIndex(processor => registration.InsertBefore.IsInstanceOfType(processor));
            if (anchor < 0)
            {
                throw new InvalidOperationException(
                    $"{nameof(ExportHandler)}.GetProcessors yields no {registration.InsertBefore.Name}, so the "
                    + $"asset processor registered by {registration.Factory.Method.DeclaringType?.Name} has no "
                    + "insertion point. Re-anchor that registration against the current upstream pipeline.");
            }
            processors.InsertRange(anchor, registration.Factory(settings));
        }
        return Timed(processors);
    }

    /// <summary>
    /// Every processor behind a stopwatch, with the whole list printed slowest first when the
    /// last one has run. The export stage already states its cost per file extension, and that
    /// is how a stage found to be writing megabytes nobody read gave itself away; the processing
    /// stage stated only the names of its steps, so its cost could only be guessed at.
    /// </summary>
    private static List<IAssetProcessor> Timed(List<IAssetProcessor> processors)
    {
        List<(string Name, double Ms)> costs = new(processors.Count);
        List<IAssetProcessor> timed = new(processors.Count);
        for (int index = 0; index < processors.Count; index++)
        {
            timed.Add(new TimedProcessor(processors[index], costs, index == processors.Count - 1));
        }
        return timed;
    }

    private sealed class TimedProcessor(IAssetProcessor inner, List<(string Name, double Ms)> costs, bool last) : IAssetProcessor
    {
        public void Process(GameData gameData)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                inner.Process(gameData);
            }
            finally
            {
                costs.Add((inner.GetType().Name,
                    (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency));
            }
            if (!last)
            {
                return;
            }
            foreach ((string name, double ms) in costs.OrderByDescending(static cost => cost.Ms))
            {
                AssetRipper.Import.Logging.Logger.Info(AssetRipper.Import.Logging.LogCategory.Processing,
                    $"[ProcessCost] {name,-44} {ms,9:F1}ms");
            }
            costs.Clear();
        }
    }
}
