using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.AddCustomComponent;

/// <summary>
/// Summary of a user-authored, named custom PDK file discovered under the
/// <see cref="UserPdkStore"/> root: its display name, the file it lives in, and
/// the fabrication process it targets. Used to populate PDK pickers without
/// loading each file's full component list.
/// </summary>
public sealed record UserPdkInfo(string Name, string FilePath, ProcessDefinition Process);
