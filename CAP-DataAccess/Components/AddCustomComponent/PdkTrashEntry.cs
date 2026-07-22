using System;
using System.Collections.Generic;

namespace CAP_DataAccess.Components.AddCustomComponent;

public enum PdkTrashKind
{
    DeletedPdk,

    RemovedComponents,
}

public sealed record PdkTrashEntry(
    string TrashFilePath,
    string PdkName,
    PdkTrashKind Kind,
    DateTime DeletedAt,
    string OriginalLivePath,
    IReadOnlyList<string> RestorableComponentNames);
