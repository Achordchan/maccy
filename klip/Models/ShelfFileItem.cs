using System;

namespace klip.Models;

public sealed record ShelfFileItem(
    Guid Id,
    string FilePath,
    string FileName
);
