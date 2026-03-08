namespace maccy.Services;

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";

    public bool StartWithWindows { get; set; }

    public string? AuthAccessToken { get; set; }

    public string? AuthRefreshToken { get; set; }

    public string? AuthIdToken { get; set; }

    public long AuthExpiresAtUnixMs { get; set; }

    public string? AuthUserEmail { get; set; }

    public string NasAgentBaseUrl { get; set; } = string.Empty;

    public int MaxItems { get; set; } = 200;

    public int MaxMegabytes { get; set; } = 300;

    public bool CaptureText { get; set; } = true;

    public bool CaptureImages { get; set; } = true;

    public bool CaptureFiles { get; set; } = true;

    public string CaptureFileExtensions { get; set; } = ".pdf,.ppt,.pptx,.doc,.docx,.xls,.xlsx,.txt,.md,.csv,.zip,.rar,.7z,.png,.jpg,.jpeg,.gif,.bmp,.webp";

    public int CaptureFileMaxMegabytes { get; set; } = 20;

    public bool MergeDuplicates { get; set; } = true;

    public bool ExcludePinnedFromLimits { get; set; } = true;

    public bool ShelfEnabled { get; set; } = true;

    public string ShelfTriggerModifier { get; set; } = "Ctrl";
}
