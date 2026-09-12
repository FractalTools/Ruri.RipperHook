using System;
using System.Collections.Concurrent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;
using Ruri.RipperHook.Core.TypeTree;

namespace Ruri.RipperHook.AR;

/// <summary>
/// 非官方引擎类的悬空引用改用**自述 guid**,不再与真正的"资产丢了"共用 deadbeef。
///
/// 上游对没有导出集合的引用一律发 <see cref="UnityGuid.MissingReference"/>
/// (<c>0000000deadbeef15deadf00d0000000</c>),于是「fork 私有引擎类没导出」与「依赖闭包真缺一份资产」
/// 在产物里长得一模一样 —— 消费侧只能靠猜,实测已经把一个 MultiCollider 当成丢失的贴图查了半天。
///
/// 本 hook 只改 guid 的取值:类 id 不在 <see cref="ClassIDType"/>(= AR 生成的官方 Unity 类全集)里时,
/// 发 <c>f0rkc1a55&lt;classId 的 8 位十六进制&gt;</c> + 补零。看到这个 guid 就知道:去 tpk / 游戏 RTTI dump
/// (<c>TypeTree/&lt;major&gt;/&lt;版本&gt;/classes.json</c>)按这个 id 查类名,再判断那份数据要不要。
/// 绝大多数 fork 附加类(碰撞体/遮挡体)本就与消费侧无关,真需要的按 shader 那套回填即可。
/// </summary>
public class ForkClassGuidHook : CommonHook, IHookModule
{
    public const string Marker = "f0rkc1a55";

    private const int GuidHexLength = 32;

    private static readonly ConcurrentDictionary<int, byte> Reported = new();

    public void OnApply()
    {
    }

    [RetargetMethod(typeof(MetaPtr), nameof(MetaPtr.CreateMissingReference))]
    public static MetaPtr CreateMissingReference(int classID, AssetType assetType)
    {
        return new MetaPtr(ExportIdHandler.GetMainExportID(classID), GuidFor(classID), assetType);
    }

    public static bool IsForkClass(int classID) => !Enum.IsDefined(typeof(ClassIDType), classID);

    public static UnityGuid GuidFor(int classID)
    {
        if (!IsForkClass(classID))
        {
            return UnityGuid.MissingReference;
        }

        string text = Marker + classID.ToString("x8");
        UnityGuid guid = UnityGuid.Parse(text.PadRight(GuidHexLength, '0'));
        Report(classID, guid);
        return guid;
    }

    private static void Report(int classID, UnityGuid guid)
    {
        if (!Reported.TryAdd(classID, 0))
        {
            return;
        }

        Console.WriteLine(
            $"    [+] ForkClassGuid: classId {classID} 不是官方 Unity 类,引用发 {guid} 而非 deadbeef"
            + $"({ClassName(classID) ?? "类名未知,查 TypeTree/<major>/<版本>/classes.json"})");
    }

    private static string? ClassName(int classID)
    {
        try
        {
            return TypeTreeDatabase.GetReleaseRoot((ClassIDType)classID, TypeTreeDatabase.ActiveVersion)?.TypeName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
