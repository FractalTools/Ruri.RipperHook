using AssetRipper.Checksum;
using AssetRipper.IO.Endian;
using AssetRipper.SourceGenerated.Classes.ClassID_91;
using Ruri.SourceGenerated.Classes.ClassID_91;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_1_1_9_Hook
{
    [RetargetMethod(typeof(AnimatorController_2018_3), "ReadRelease")]
    public void AnimatorController_2021_ReadRelease(ref EndianSpanReader reader)
    {
        var _this = (object)this as AnimatorController_2018_3;

        var dummyThis = new AnimatorController_2021(_this.AssetInfo);
        dummyThis.ReadRelease(ref reader);

        ReflectionExtensions.ClassDeepCopy(dummyThis, _this);
        
        _this.m_TOS.Clear();

        // 这里的 dummyThis.m_TOSData 是 1.1.9 特有的字段
        foreach (var entry in dummyThis.m_TOSData)
        {
            // 通过 CRC32 计算 Key，存入 _this 的 Dictionary 中
            uint key = Crc32Algorithm.HashUTF8(entry.String);

            if (!_this.m_TOS.ContainsKey(key))
            {
                _this.m_TOS.Add(key, entry);
            }
        }
    }
}
