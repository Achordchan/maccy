using System;

namespace maccy.Models;

public sealed record ShelfFileItem(
    Guid Id,
    string FilePath,
    string FileName
);
