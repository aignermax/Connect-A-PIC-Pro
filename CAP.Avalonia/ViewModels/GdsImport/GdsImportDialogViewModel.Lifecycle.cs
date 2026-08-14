using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.GdsImport;

/// <summary>
/// The run-lifecycle half of <see cref="GdsImportDialogViewModel"/>: the cancel
/// command, window-close semantics and the per-run cancellation-source handling
/// (split out to keep the dialog ViewModel below the architecture size limit;
/// sibling partials: .Options.cs, .Census.cs, .Summary.cs).
/// </summary>
public partial class GdsImportDialogViewModel
{
    /// <summary>Cancels the running operation; closes the dialog when idle or completed.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
        {
            CancelCurrentRun();
            return;
        }
        OnClose?.Invoke();
    }

    /// <summary>
    /// Called by the view when the dialog window closes: cancels the running
    /// operation (if any) and releases the per-run cancellation source. A close
    /// mid-ANALYSIS must not leave the background run mutating a canvas the
    /// user no longer sees. A close after the Import button started the import
    /// is the deliberate auto-close (<see cref="_continueRunAfterWindowClose"/>):
    /// the import finishes in the background and mirrors its report to the
    /// error console; the run's own finally disposes the source.
    /// </summary>
    public void OnWindowClosed()
    {
        if (_continueRunAfterWindowClose)
            return;
        CancelCurrentRun();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Set when the import started and the window closed on purpose:
    /// the run continues past the window's lifetime.</summary>
    private bool _continueRunAfterWindowClose;

    /// <summary>
    /// Replaces the per-run cancellation source, disposing the previous run's, and
    /// returns the new source. Both entry points no-op while <see cref="IsBusy"/>,
    /// so the replaced source always belongs to a finished run — but a late
    /// progress callback, a queued await continuation or a FileStream read of the
    /// service's off-thread parse may still REFERENCE its token. Cancel BEFORE
    /// dispose: every .NET registration path (stream reads, task cancellation
    /// wiring, semaphore waits) short-circuits on an already-cancelled source,
    /// while registering on a disposed-not-cancelled source throws
    /// <see cref="ObjectDisposedException"/> ("The CancellationTokenSource has
    /// been disposed") — the import failure this ordering prevents.
    /// </summary>
    private CancellationTokenSource ResetCancellationSource()
    {
        CancelCurrentRun();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts;
    }

    /// <summary>
    /// Cancels the current run's source, tolerating one that a racing reset or
    /// window close already disposed: <see cref="CancellationTokenSource.Cancel"/>
    /// throws <see cref="ObjectDisposedException"/> on a disposed source, which
    /// must never escape as an import failure (the run is over either way).
    /// </summary>
    private void CancelCurrentRun()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The source was already released by a racing reset/close — nothing to cancel.
        }
    }

    /// <summary>Test seam (InternalsVisibleTo UnitTests): the current per-run cancellation source.</summary>
    internal CancellationTokenSource? CurrentCts => _cts;

    /// <summary>
    /// Test seam (InternalsVisibleTo UnitTests): invoked between the import
    /// service completing and canvas placement starting, so tests can land a
    /// window close deterministically inside that otherwise load-dependent gap.
    /// </summary>
    internal Action? ImportServiceCompletedTestHook { get; set; }
}
