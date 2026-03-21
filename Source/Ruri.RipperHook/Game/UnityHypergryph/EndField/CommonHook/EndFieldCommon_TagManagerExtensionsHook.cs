using AssetRipper.SourceGenerated.Classes.ClassID_78;
using AssetRipper.SourceGenerated.Extensions;

namespace Ruri.RipperHook.Endfield;

public partial class EndFieldCommon_Hook : RipperHook
{
    [RetargetMethod(typeof(TagManagerExtensions), nameof(TagManagerExtensions.TagIDToName))]
    public static string TagIDToName(ITagManager? tagManager, int tagID)
    {
		switch (tagID)
		{
			case 0:
				return TagManagerConstants.UntaggedTag;
			case 1:
				return TagManagerConstants.RespawnTag;
			case 2:
				return TagManagerConstants.FinishTag;
			case 3:
				return TagManagerConstants.EditorOnlyTag;
			//case 4:
			case 5:
				return TagManagerConstants.MainCameraTag;
			case 6:
				return TagManagerConstants.PlayerTag;
			case 7:
				return TagManagerConstants.GameControllerTag;
		}
		if (tagManager != null)
		{
			// Unity doesn't verify tagID on export?
			int tagIndex = tagID - 20000;
			if (tagIndex < tagManager.Tags.Count)
			{
				if (tagIndex >= 0)
				{
					return tagManager.Tags[tagIndex].String;
				}
				else if (!tagManager.IsBrokenCustomTags())
				{
                    return $"unknown_{tagID}";
                }
            }
        }
        return $"unknown_{tagID}";
    }
}