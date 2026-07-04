using Avalonia.Controls.Notifications;
using CAP.Avalonia.Services.Notifications;
using Shouldly;
using Xunit;

namespace UnitTests.Services.Notifications;

/// <summary>
/// Tests for <see cref="NotificationService"/> — the toast service behind the
/// main-window WindowNotificationManager (issue #586). Avalonia's
/// <c>INotificationManager</c> is not implementable by user code, so the tests
/// attach a recording delegate via the delegate overload of Attach.
/// </summary>
public class NotificationServiceTests
{
    private readonly NotificationService _service = new();
    private readonly List<Notification> _shown = new();

    private void AttachRecorder() => _service.Attach(_shown.Add);

    [Fact]
    public void ShowInfo_AfterAttach_ForwardsMessageWithDefaultTitle()
    {
        AttachRecorder();

        _service.ShowInfo("FDTD recompute cancelled.");

        var toast = _shown.ShouldHaveSingleItem();
        toast.Message.ShouldBe("FDTD recompute cancelled.");
        toast.Title.ShouldNotBeNullOrWhiteSpace();
        toast.Type.ShouldBe(NotificationType.Information);
    }

    [Fact]
    public void Show_UsesCustomTitle_WhenProvided()
    {
        AttachRecorder();

        _service.ShowSuccess("S-matrix applied.", "FDTD");

        _shown.ShouldHaveSingleItem().Title.ShouldBe("FDTD");
    }

    [Theory]
    [InlineData("info", NotificationType.Information)]
    [InlineData("success", NotificationType.Success)]
    [InlineData("warning", NotificationType.Warning)]
    public void SeverityMethods_MapToMatchingNotificationType(string severity, NotificationType expected)
    {
        AttachRecorder();

        switch (severity)
        {
            case "info": _service.ShowInfo("m"); break;
            case "success": _service.ShowSuccess("m"); break;
            case "warning": _service.ShowWarning("m"); break;
        }

        _shown.ShouldHaveSingleItem().Type.ShouldBe(expected);
    }

    [Fact]
    public void Toasts_AutoDismiss_ExpirationIsSet()
    {
        AttachRecorder();

        _service.ShowInfo("transient");

        var toast = _shown.ShouldHaveSingleItem();
        toast.Expiration.ShouldBe(NotificationService.DefaultExpiration);
        toast.Expiration.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceMessages_AreIgnored(string message)
    {
        AttachRecorder();

        _service.ShowInfo(message);

        _shown.ShouldBeEmpty();
    }

    [Fact]
    public void ToastsRaisedBeforeAttach_AreFlushedInOrderOnAttach()
    {
        _service.ShowInfo("first");
        _service.ShowSuccess("second");

        AttachRecorder();

        _shown.Count.ShouldBe(2);
        _shown[0].Message.ShouldBe("first");
        _shown[1].Message.ShouldBe("second");
    }

    [Fact]
    public void PendingBuffer_IsCapped_ExcessToastsAreDropped()
    {
        for (int i = 0; i < NotificationService.MaxPendingNotifications + 5; i++)
            _service.ShowInfo($"toast {i}");

        AttachRecorder();

        _shown.Count.ShouldBe(NotificationService.MaxPendingNotifications);
        _shown[0].Message.ShouldBe("toast 0");
    }

    [Fact]
    public void PendingBuffer_IsNotReplayedTwice()
    {
        _service.ShowInfo("buffered");
        AttachRecorder();

        var secondSink = new List<Notification>();
        _service.Attach(secondSink.Add);

        secondSink.ShouldBeEmpty();
    }
}
