using CUE4Parse.UE4.Assets.Exports;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

public interface IComponentExporter
{
    bool CanExport(UObject component);

    void Export(in PlacedComponent placed, GlbSceneContext context);
}
