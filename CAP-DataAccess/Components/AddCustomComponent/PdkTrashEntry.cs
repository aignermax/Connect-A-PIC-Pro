using System;
using System.Collections.Generic;

namespace CAP_DataAccess.Components.AddCustomComponent;

/// <summary>
/// What a single <c>.trash</c> file represents, inferred from whether its original PDK file
/// still lives under the store root (see <see cref="PdkTrashService"/>).
/// </summary>
public enum PdkTrashKind
{
    /// <summary>The whole PDK was deleted (its live file is gone); restoring brings the file back.</summary>
    DeletedPdk,

    /// <summary>
    /// A pre-edit backup left when one or more components were removed while the PDK itself
    /// still exists; restoring re-adds the removed components to the live file.
    /// </summary>
    RemovedComponents,
}

/// <summary>
/// One recoverable item in the user-PDK trash: a deleted PDK, or a backup snapshot from which
/// removed components can be restored. Purely descriptive — <see cref="PdkTrashService"/> reads
/// these and performs the restore; the UI layer re-registers the result into the library.
/// </summary>
/// <param name="TrashFilePath">Full path of the backup file under <c>&lt;root&gt;/.trash</c>.</param>
/// <param name="PdkName">Display name of the PDK stored in the backup file.</param>
/// <param name="Kind">Whether this is a deleted PDK or a removed-components backup.</param>
/// <param name="DeletedAt">Timestamp parsed from the trash file name (local time).</param>
/// <param name="OriginalLivePath">Where the PDK file lives (or would live) under the store root.</param>
/// <param name="RestorableComponentNames">
/// For <see cref="PdkTrashKind.RemovedComponents"/>: the components present in the backup but
/// missing from the current live file (i.e. actually restorable). For
/// <see cref="PdkTrashKind.DeletedPdk"/>: all components in the deleted PDK.
/// </param>
public sealed record PdkTrashEntry(
    string TrashFilePath,
    string PdkName,
    PdkTrashKind Kind,
    DateTime DeletedAt,
    string OriginalLivePath,
    IReadOnlyList<string> RestorableComponentNames);
