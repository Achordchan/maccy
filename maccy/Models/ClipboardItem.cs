using System;
using System.Collections.Generic;

namespace maccy.Models;

public sealed record ClipboardItem(
    Guid Id,
    ClipboardContentKind Kind,
    DateTimeOffset CapturedAt,
    long ApproxBytes,
    string? Text,
    string? ImageFilePath,
    IReadOnlyList<string>? FilePaths,
    bool Pinned,
    int CopyCount = 1,
    DateTimeOffset? FirstCapturedAt = null,
    string? Note = null,
    string? ContentHash = null,
    string? SourceAppName = null,
    string? SourceAppPath = null
);
