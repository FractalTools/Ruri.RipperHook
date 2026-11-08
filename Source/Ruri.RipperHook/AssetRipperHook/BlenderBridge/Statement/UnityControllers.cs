using System.Text.Json.Nodes;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Classes.ClassID_1101;
using AssetRipper.SourceGenerated.Classes.ClassID_1102;
using AssetRipper.SourceGenerated.Classes.ClassID_1107;
using AssetRipper.SourceGenerated.Classes.ClassID_1109;
using AssetRipper.SourceGenerated.Classes.ClassID_206;
using AssetRipper.SourceGenerated.Classes.ClassID_207;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Classes.ClassID_91;
using AssetRipper.SourceGenerated.NativeEnums.Animation;
using AssetRipper.SourceGenerated.Subclasses.AnimatorCondition;
using AssetRipper.SourceGenerated.Subclasses.AnimatorControllerLayer;
using AssetRipper.SourceGenerated.Subclasses.AnimatorControllerParameter;
using AssetRipper.SourceGenerated.Subclasses.ChildAnimatorState;
using AssetRipper.SourceGenerated.Subclasses.ChildAnimatorStateMachine;
using AssetRipper.SourceGenerated.Subclasses.ChildMotion;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// An animator controller as the record a host reads: what the controller states, in the order and under the
/// numbers a host can refer to it by. States and state machines share one numbering (their position in a walk of the
/// layers), a transition names its destination by that number, and a clip is named by the key the clip statement
/// states for it. Beside every field a host models, the record carries the fields it does not -- mirror, cycle offset,
/// tags, behaviours, solo and mute, the interruption sources and blend tree kinds a host may lack -- with the
/// controller's own values, so that a host which cannot play one refuses it instead of losing it.
/// </summary>
public sealed class UnityControllers
{
    private readonly Func<IUnityObjectBase, string> _keyOf;
    private readonly Dictionary<IUnityObjectBase, int> _ids = new(ReferenceEqualityComparer.Instance);
    private int _next;

    private UnityControllers(Func<IUnityObjectBase, string> keyOf)
    {
        _keyOf = keyOf;
    }

    public static JsonObject Describe(IAnimatorController controller, Func<IUnityObjectBase, string> keyOf) =>
        new UnityControllers(keyOf).Record(controller);

    private JsonObject Record(IAnimatorController controller)
    {
        List<IAnimatorStateMachine> roots = [];
        foreach (IAnimatorControllerLayer layer in controller.AnimatorLayers)
        {
            if (layer.StateMachine.TryGetAsset(controller.Collection) is { } machine)
            {
                roots.Add(machine);
                Number(machine);
            }
        }

        JsonArray parameters = [];
        foreach (IAnimatorControllerParameter parameter in controller.AnimatorParameters)
        {
            parameters.Add(new JsonObject
            {
                ["name"] = parameter.Name.String,
                ["type"] = ((AnimatorControllerParameterType)parameter.Type).ToString(),
                ["defaultFloat"] = parameter.DefaultFloat,
                ["defaultInteger"] = parameter.DefaultInt,
                ["defaultBool"] = parameter.DefaultBool,
            });
        }

        JsonArray layers = [];
        foreach (IAnimatorControllerLayer layer in controller.AnimatorLayers)
        {
            IAnimatorStateMachine? machine = layer.StateMachine.TryGetAsset(controller.Collection);
            JsonObject entry = new()
            {
                ["name"] = layer.Name.String,
                ["weight"] = layer.DefaultWeight,
                ["blendMode"] = layer.BlendingModeE.ToString(),
                ["ikPass"] = layer.IKPass,
                ["syncedLayerIndex"] = layer.SyncedLayerIndex,
                ["syncedLayerAffectsTiming"] = layer.SyncedLayerAffectsTiming,
                ["hasAvatarMask"] = layer.Mask.TryGetAsset(controller.Collection) is not null,
                ["motionOverrides"] = layer.Has_Motions() ? layer.Motions.Count : 0,
                ["behaviourCount"] = layer.Has_Behaviours() ? layer.Behaviours.Count : 0,
            };
            if (machine is not null)
            {
                entry["stateMachine"] = Machine(machine);
            }
            layers.Add(entry);
        }

        return new JsonObject
        {
            ["sourceKey"] = _keyOf(controller),
            ["sourceName"] = controller.Name.String,
            ["parameters"] = parameters,
            ["layers"] = layers,
        };
    }

    private void Number(IAnimatorStateMachine machine)
    {
        if (!_ids.TryAdd(machine, _next))
        {
            return;
        }
        _next++;
        foreach (IAnimatorState state in StatesOf(machine))
        {
            if (_ids.TryAdd(state, _next))
            {
                _next++;
            }
        }
        foreach (IAnimatorStateMachine child in ChildrenOf(machine))
        {
            Number(child);
        }
    }

    private static IEnumerable<IAnimatorState> StatesOf(IAnimatorStateMachine machine)
    {
        if (machine.Has_ChildStates())
        {
            foreach (ChildAnimatorState child in machine.ChildStates)
            {
                if (child.State.TryGetAsset(machine.Collection) is { } state)
                {
                    yield return state;
                }
            }
        }
        else
        {
            foreach (IAnimatorState? state in machine.StatesP)
            {
                if (state is not null)
                {
                    yield return state;
                }
            }
        }
    }

    private static IEnumerable<IAnimatorStateMachine> ChildrenOf(IAnimatorStateMachine machine)
    {
        if (machine.Has_ChildStateMachines())
        {
            foreach (ChildAnimatorStateMachine child in machine.ChildStateMachines)
            {
                if (child.StateMachine.TryGetAsset(machine.Collection) is { } nested)
                {
                    yield return nested;
                }
            }
        }
        else
        {
            foreach (IAnimatorStateMachine? nested in machine.ChildStateMachineP)
            {
                if (nested is not null)
                {
                    yield return nested;
                }
            }
        }
    }

    private int IdOf(IUnityObjectBase target, string what)
    {
        if (_ids.TryGetValue(target, out int id))
        {
            return id;
        }
        throw new InvalidOperationException($"{what} {target.ClassName}/{target.PathID} is named by a transition but is not in the controller's hierarchy.");
    }

    private JsonObject Machine(IAnimatorStateMachine machine)
    {
        JsonObject record = new()
        {
            ["id"] = IdOf(machine, "state machine"),
            ["name"] = machine.Name.String,
            ["behaviourCount"] = machine.Has_StateMachineBehaviours() ? machine.StateMachineBehavioursP.Count : 0,
        };
        if (machine.DefaultStateP is { } defaultState)
        {
            record["defaultState"] = IdOf(defaultState, "default state");
        }

        JsonArray states = [];
        foreach (IAnimatorState state in StatesOf(machine))
        {
            states.Add(State(state));
        }
        record["states"] = states;

        JsonArray subMachines = [];
        foreach (IAnimatorStateMachine nested in ChildrenOf(machine))
        {
            subMachines.Add(Machine(nested));
        }
        record["subStateMachines"] = subMachines;

        JsonArray anyState = [];
        foreach (IAnimatorStateTransition? transition in machine.AnyStateTransitionsP)
        {
            if (transition is not null)
            {
                anyState.Add(StateTransition(transition));
            }
        }
        record["anyStateTransitions"] = anyState;

        JsonArray entry = [];
        foreach (IAnimatorTransition? transition in machine.EntryTransitionsP)
        {
            if (transition is not null)
            {
                entry.Add(SelectorTransition(transition));
            }
        }
        record["entryTransitions"] = entry;

        JsonArray fromMachines = [];
        if (machine.StateMachineTransitions is { } pairs)
        {
            foreach (var pair in pairs)
            {
                IAnimatorStateMachine? source = pair.Key.TryGetAsset(machine.Collection);
                if (source is null)
                {
                    continue;
                }
                JsonArray transitions = [];
                foreach (var pointer in pair.Value)
                {
                    if (pointer.TryGetAsset(machine.Collection) is { } transition)
                    {
                        transitions.Add(SelectorTransition(transition));
                    }
                }
                fromMachines.Add(new JsonObject { ["stateMachine"] = IdOf(source, "state machine"), ["transitions"] = transitions });
            }
        }
        record["stateMachineTransitions"] = fromMachines;
        return record;
    }

    private JsonObject State(IAnimatorState state)
    {
        JsonObject record = new()
        {
            ["id"] = IdOf(state, "state"),
            ["name"] = state.Name.String,
            ["speed"] = state.Speed,
            ["mirror"] = state.Mirror,
            ["cycleOffset"] = state.CycleOffset,
            ["writeDefaultValues"] = state.WriteDefaultValues,
            ["ikOnFeet"] = state.IKOnFeet,
            ["behaviourCount"] = state.Has_StateMachineBehaviours() ? state.StateMachineBehavioursP.Count : 0,
        };
        Active(record, "speedParameter", state.SpeedParameterActive, state.SpeedParameter.String);
        Active(record, "mirrorParameter", state.MirrorParameterActive, state.MirrorParameter.String);
        Active(record, "cycleOffsetParameter", state.CycleOffsetParameterActive, state.CycleOffsetParameter.String);
        Active(record, "timeParameter", state.TimeParameterActive, state.TimeParameter.String);
        if (state.Tag.String.Length > 0)
        {
            record["tag"] = state.Tag.String;
        }

        IMotion? motion = state.Has_Motion() ? state.MotionP : state.MotionsP.Count > 0 ? state.MotionsP[0] : null;
        switch (motion)
        {
            case IAnimationClip clip:
                record["motionKind"] = "Clip";
                record["clip"] = _keyOf(clip);
                record["clipLoop"] = clip.MuscleClip_C74?.LoopTime ?? false;
                break;
            case IBlendTree tree:
                record["motionKind"] = "BlendTree";
                record["blendTree"] = BlendTree(tree);
                break;
            default:
                record["motionKind"] = "None";
                break;
        }

        JsonArray transitions = [];
        foreach (IAnimatorStateTransition? transition in state.TransitionsP)
        {
            if (transition is not null)
            {
                transitions.Add(StateTransition(transition));
            }
        }
        record["transitions"] = transitions;
        return record;
    }

    private static void Active(JsonObject record, string name, bool active, string value)
    {
        if (active)
        {
            record[name] = value;
        }
    }

    private JsonObject BlendTree(IBlendTree tree)
    {
        int type = tree.Has_BlendType_Int32() ? tree.BlendType_Int32 : unchecked((int)tree.BlendType_UInt32);
        JsonObject record = new()
        {
            ["type"] = type switch
            {
                0 => "Simple1D",
                1 => "SimpleDirectional2D",
                2 => "FreeformDirectional2D",
                3 => "FreeformCartesian2D",
                4 => "Direct",
                _ => "Unknown" + type,
            },
            ["minThreshold"] = tree.MinThreshold,
            ["maxThreshold"] = tree.MaxThreshold,
            ["useAutomaticThresholds"] = tree.UseAutomaticThresholds,
            ["normalizedBlendValues"] = tree.NormalizedBlendValues,
        };
        if (tree.BlendParameter.String.Length > 0)
        {
            record["parameterX"] = tree.BlendParameter.String;
        }
        if (tree.BlendParameterY.String.Length > 0)
        {
            record["parameterY"] = tree.BlendParameterY.String;
        }

        JsonArray children = [];
        foreach (IChildMotion child in tree.Childs)
        {
            JsonObject entry = new()
            {
                ["threshold"] = child.Threshold,
                ["positionX"] = child.Position.X,
                ["positionY"] = child.Position.Y,
                ["timeScale"] = child.TimeScale,
                ["cycleOffset"] = child.CycleOffset,
                ["mirror"] = child.Mirror,
            };
            if (child.DirectBlendParameter.String.Length > 0)
            {
                entry["directBlendParameter"] = child.DirectBlendParameter.String;
            }
            switch (child.Motion.TryGetAsset(tree.Collection))
            {
                case IAnimationClip clip:
                    entry["clip"] = _keyOf(clip);
                    entry["clipLoop"] = clip.MuscleClip_C74?.LoopTime ?? false;
                    break;
                case IBlendTree nested:
                    entry["blendTree"] = BlendTree(nested);
                    break;
            }
            children.Add(entry);
        }
        record["children"] = children;
        return record;
    }

    private JsonObject StateTransition(IAnimatorStateTransition transition)
    {
        JsonObject record = new()
        {
            ["conditions"] = Conditions(transition.Conditions),
            ["hasExitTime"] = transition.HasExitTime,
            ["exitTime"] = transition.ExitTime,
            ["hasFixedDuration"] = transition.HasFixedDuration,
            ["duration"] = transition.TransitionDuration,
            ["offset"] = transition.TransitionOffset,
            ["interruptionSource"] = transition.InterruptionSourceE.ToString(),
            ["orderedInterruption"] = transition.OrderedInterruption,
            ["canTransitionToSelf"] = transition.CanTransitionToSelf,
            ["mute"] = transition.Mute,
            ["solo"] = transition.Solo,
        };
        Destination(record, transition.DestinationStateP, transition.DestinationStateMachineP, transition.IsExit);
        return record;
    }

    private JsonObject SelectorTransition(IAnimatorTransition transition)
    {
        JsonObject record = new()
        {
            ["conditions"] = Conditions(transition.Conditions),
            ["mute"] = transition.Mute,
            ["solo"] = transition.Solo,
        };
        Destination(record, transition.DestinationStateP, transition.DestinationStateMachineP, transition.IsExit);
        return record;
    }

    private void Destination(JsonObject record, IAnimatorState? state, IAnimatorStateMachine? machine, bool isExit)
    {
        if (isExit)
        {
            record["destinationKind"] = "Exit";
        }
        else if (state is not null)
        {
            record["destinationKind"] = "State";
            record["destination"] = IdOf(state, "destination state");
        }
        else if (machine is not null)
        {
            record["destinationKind"] = "StateMachine";
            record["destination"] = IdOf(machine, "destination state machine");
        }
        else
        {
            record["destinationKind"] = "None";
        }
    }

    private static JsonArray Conditions(IEnumerable<IAnimatorCondition> conditions)
    {
        JsonArray result = [];
        foreach (IAnimatorCondition condition in conditions)
        {
            result.Add(new JsonObject
            {
                ["parameter"] = condition.ConditionEvent.String,
                ["mode"] = ConditionModeName(condition.ConditionMode, condition.ConditionEvent.String),
                ["threshold"] = condition.EventTreshold,
            });
        }
        return result;
    }

    /// <summary>
    /// A condition mode under the name a host reads (If, IfNot, Greater, Less, Equals, NotEqual), keyed by the number
    /// the controller serializes rather than by the generated enum's member names, which carry a prefix. The legacy
    /// exit-time number 5 and any number a host cannot evaluate are refused, not written under some other name.
    /// </summary>
    private static string ConditionModeName(int mode, string parameter) => mode switch
    {
        1 => "If",
        2 => "IfNot",
        3 => "Greater",
        4 => "Less",
        6 => "Equals",
        7 => "NotEqual",
        _ => throw new InvalidOperationException(
            $"condition on parameter '{parameter}' has mode {mode}, which is none of If(1), IfNot(2), Greater(3), Less(4), Equals(6), NotEqual(7)."),
    };
}
