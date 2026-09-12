using System.Numerics;

namespace Ruri.RipperHook.Humanoid;

public sealed class MuscleBone
{
    public string Path { get; }
    public int NodeIndex { get; }
    public int Slot { get; }
    public Quaternion PreQ { get; }
    public Quaternion PostQ { get; }
    public Vector3 Sgn { get; }
    public Vector3 LimitMin { get; }
    public Vector3 LimitMax { get; }
    public bool IsHips { get; internal set; }

    private readonly string?[] _dofMuscles = new string?[3];

    internal readonly int[] DofChannel = { -1, -1, -1 };

    internal MuscleBone(string path, int nodeIndex, int slot, Quaternion preQ, Quaternion postQ, Vector3 sgn,
        Vector3 limitMin, Vector3 limitMax)
    {
        Path = path;
        NodeIndex = nodeIndex;
        Slot = slot;
        PreQ = preQ;
        PostQ = postQ;
        Sgn = sgn;
        LimitMin = limitMin;
        LimitMax = limitMax;
    }

    internal void SetDofMuscle(int axis, string muscle) => _dofMuscles[axis] = muscle;

    public string? GetDofMuscle(int axis) => _dofMuscles[axis];

    internal void BindDofChannels(IReadOnlyDictionary<string, int> channelIndex)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            string? muscle = _dofMuscles[axis];
            DofChannel[axis] = muscle is not null && channelIndex.TryGetValue(muscle, out int column)
                ? column
                : -1;
        }
    }
}
