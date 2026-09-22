using System.Collections.Generic;

namespace Ruri.RipperHook.HookUtils.GameBundleHook;

/// <summary>
/// What a scan learns about one archive.
///
/// <para><see cref="Facts"/> is what a decoder knows and the scan cannot: a statement the archive's
/// own assets make about themselves, harvested while the archive is open. It exists because the
/// alternative is reading those assets again when a question is asked -- for one title that is
/// 1.2GB of archives to answer "which characters are there", against nothing at all when the scan
/// that already read them wrote the answer down.</para>
///
/// <para>Facts are opaque here on purpose. The scan does not know what a decoder's facts mean and
/// does not need to; it stores them beside the archive and a query filters on them by text. Giving
/// the kernel a schema for them would make every new kind of fact a kernel change.</para>
/// </summary>
public readonly record struct CabRow(
    string Cab,
    string FileName,
    List<string> Dependencies,
    List<int> ClassIds,
    List<string> ContainerPaths,
    List<string> Facts);
