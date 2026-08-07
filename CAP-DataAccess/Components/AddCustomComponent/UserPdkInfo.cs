using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.AddCustomComponent;

/// <summary>
/// A user-managed PDK living in the store's root directory.
/// <paramref name="Process"/> is null for process-agnostic PDKs (e.g. created by a
/// GDS import): they declare no fabrication process, so their components stay
/// placeable under every active process — but they are still user-defined and editable.
/// </summary>
public sealed record UserPdkInfo(string Name, string FilePath, ProcessDefinition? Process);
