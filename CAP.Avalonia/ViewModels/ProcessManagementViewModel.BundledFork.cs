using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CAP.Avalonia.Services.Localization;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels;

/// <summary>
/// Bundled-fork half of the process editor (issue #786): the first save on a bundled
/// read-only PDK implicitly writes the user's custom copy (fork) into the managed root —
/// the bundled JSON is never touched — plus the "metal may cross photonic" checkbox that
/// maps to <see cref="ProcessDefinition.ElectricalBridgeRequired"/> inverted.
/// Extracted from <c>ProcessManagementViewModel.cs</c> to keep it under the 500-line
/// hard limit (issue #789 tracks any further split).
/// </summary>
public partial class ProcessManagementViewModel
{
    /// <summary>The store forks of bundled PDKs are written to (managed user root).</summary>
    private readonly UserPdkStore _userPdkStore;

    /// <summary>The fork created by an implicit bundled-source save; later saves write to it directly.</summary>
    private PdkDraft? _forkedDraft;
    private string? _forkPath;

    /// <summary>
    /// Checked when metal traces may cross photonic waveguides directly; unchecked means every
    /// metal/waveguide crossing needs a bridge. Maps to <see cref="ProcessDefinition.ElectricalBridgeRequired"/>
    /// inverted — null/absent and false both mean direct crossing, so checked writes null to keep
    /// unedited files byte-identical (the saver drops nulls).
    /// </summary>
    [ObservableProperty]
    private bool _metalMayCrossPhotonic = true;

    /// <summary>
    /// Raised when a save on a bundled read-only PDK implicitly created the user's custom
    /// copy (fork). The UI layer swaps the library entry to the fork — it keeps the bundled
    /// name, so the active process keeps resolving it by value.
    /// </summary>
    public event EventHandler<BundledPdkForkSavedEventArgs>? BundledPdkForkSaved;

    /// <summary>
    /// First save on a bundled read-only PDK: writes the edited process (with all of the
    /// bundled PDK's components) as the user's fork in the managed root — the bundled JSON
    /// is never touched — and points the editor at the fork for the rest of the session.
    /// </summary>
    private void SaveAsFork(PdkDraft bundledDraft)
    {
        try
        {
            var edited = ToProcess();
            // Only rows owned by this PDK — a foreign PDK pulled in for reference via
            // ImportFromPdk must never be written into the fork either.
            edited.Layers = edited.Layers.Where(l => _ownedRows.Contains(l)).ToList();
            edited.Xsections = edited.Xsections.Where(x => _ownedRows.Contains(x)).ToList();
            edited.Materials = edited.Materials.Where(m => _ownedRows.Contains(m)).ToList();
            var forkDraft = new PdkDraft
            {
                Name = bundledDraft.Name,
                Description = bundledDraft.Description,
                Foundry = bundledDraft.Foundry,
                Version = bundledDraft.Version,
                DefaultWavelengthNm = bundledDraft.DefaultWavelengthNm,
                ProcessAgnostic = bundledDraft.ProcessAgnostic,
                NazcaModuleName = bundledDraft.NazcaModuleName,
                Backend = bundledDraft.Backend,
                GdsFactoryRoutingCrossSection = bundledDraft.GdsFactoryRoutingCrossSection,
                Components = new List<PdkComponentDraft>(bundledDraft.Components),
                Process = edited,
            };
            var forkPath = _userPdkStore.SaveDraftAsFork(forkDraft, bundledDraft.Name);
            forkDraft.FilePath = forkPath;
            _forkedDraft = forkDraft;
            _forkPath = forkPath;
            _memberDrafts = new List<PdkDraft> { forkDraft };

            BundledPdkForkSaved?.Invoke(this, new BundledPdkForkSavedEventArgs(bundledDraft.Name, forkPath));
            StatusText = string.Format(
                LocalizationService.Instance.Translate("ProcessMgmt.Status.SavedAsFork"), Path.GetFileName(forkPath));
            ProcessSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("ProcessMgmt.Status.SaveFailed"), ex.Message);
        }
    }
}

/// <summary>Payload of <see cref="ProcessManagementViewModel.BundledPdkForkSaved"/>: the bundled
/// PDK's name and the fork file the first save wrote in the managed user root.</summary>
public sealed class BundledPdkForkSavedEventArgs : EventArgs
{
    /// <summary>Creates the payload.</summary>
    public BundledPdkForkSavedEventArgs(string pdkName, string forkPath)
    {
        PdkName = pdkName;
        ForkPath = forkPath;
    }

    /// <summary>Name of the bundled PDK the fork shadows (kept identical on purpose).</summary>
    public string PdkName { get; }

    /// <summary>Full path of the written fork JSON in the managed user root.</summary>
    public string ForkPath { get; }
}
