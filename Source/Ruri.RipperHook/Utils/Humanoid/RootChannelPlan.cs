namespace Ruri.RipperHook.Humanoid;

public readonly struct RootChannelPlan
{
    public readonly int RootTx, RootTy, RootTz;
    public readonly int RootQx, RootQy, RootQz, RootQw;
    public readonly int MotionTx, MotionTy, MotionTz;
    public readonly int MotionQx, MotionQy, MotionQz, MotionQw;

    private RootChannelPlan(IReadOnlyDictionary<string, int> index)
    {
        static int Find(IReadOnlyDictionary<string, int> index, string attribute)
            => index.TryGetValue(attribute, out int column) ? column : -1;

        RootTx = Find(index, "RootT.x"); RootTy = Find(index, "RootT.y"); RootTz = Find(index, "RootT.z");
        RootQx = Find(index, "RootQ.x"); RootQy = Find(index, "RootQ.y");
        RootQz = Find(index, "RootQ.z"); RootQw = Find(index, "RootQ.w");
        MotionTx = Find(index, "MotionT.x"); MotionTy = Find(index, "MotionT.y"); MotionTz = Find(index, "MotionT.z");
        MotionQx = Find(index, "MotionQ.x"); MotionQy = Find(index, "MotionQ.y");
        MotionQz = Find(index, "MotionQ.z"); MotionQw = Find(index, "MotionQ.w");
    }

    internal static RootChannelPlan Bind(IReadOnlyDictionary<string, int> index) => new(index);

    public bool HasRootTranslation => RootTx >= 0 || RootTy >= 0 || RootTz >= 0;

    public bool HasMotion => MotionTx >= 0 || MotionTy >= 0 || MotionTz >= 0 || MotionQw >= 0;

    public static float Read(ReadOnlySpan<float> frame, int column) => column >= 0 ? frame[column] : 0f;
}
