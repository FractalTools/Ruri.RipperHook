namespace Ruri.RipperHook.Endfield.VFS;

public enum EVFSBlockType : byte
{
    None = 0,
    InitialAudio = 1,
    InitialBundle = 2,
    InitialExtendData = 3,
    BundleManifest = 4,
    IFixPatch = 5,
    AuditStreaming = 6,
    AuditDynamicStreaming = 7,
    AuditIV = 8,
    AuditAudio = 9,
    AuditVideo = 10,
    Bundle = 11,
    Audio = 12,
    Video = 13,
    IV = 14,
    Streaming = 15,
    DynamicStreaming = 16,
    Lua = 17,
    Table = 18,
    JsonData = 19,
    ExtendData = 20,
    HotfixAudio = 21,
    AudioChinese = 101,
    AudioEnglish = 102,
    AudioJapanese = 103,
    AudioKorean = 104,
    Raw = 255,
}

public enum EVFSFileTag : byte
{
    None = 0,
    Audit = 1
}

public struct FVFBlockChunkInfo
{
    public UInt128 md5Name;
    public UInt128 contentMD5;
    public long length;
    public EVFSBlockType blockType;
    public EVFSFileTag mainTag;
    public FVFBlockFileInfo[] files;
}

public struct FVFBlockFileInfo
{
    public string fileName;
    public long fileNameHash;
    public UInt128 fileChunkMD5Name;
    public UInt128 fileDataMD5;
    public long offset;
    public long len;
    public EVFSBlockType blockType;
    public bool bUseEncrypt;
    public long ivSeed;
}
