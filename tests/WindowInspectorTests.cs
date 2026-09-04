using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FullScreenManager.Tests;

[TestClass]
public sealed class WindowInspectorTests
{
    [TestMethod]
    public void ExactMonitorBoundsAreFullscreen()
    {
        var monitor = Rect(0, 0, 1920, 1080);
        Assert.IsTrue(WindowInspector.CoversMonitor(monitor, monitor));
    }

    [TestMethod]
    public void OversizedExclusiveBoundsAreFullscreen() =>
        Assert.IsTrue(WindowInspector.CoversMonitor(
            Rect(-8, -8, 1928, 1088), Rect(0, 0, 1920, 1080)));

    [TestMethod]
    public void PartialMonitorBoundsAreNotFullscreen() =>
        Assert.IsFalse(WindowInspector.CoversMonitor(
            Rect(0, 0, 1600, 900), Rect(0, 0, 1920, 1080)));

    [TestMethod]
    public void CurrentProcessIdentityMatches()
    {
        var processId = (uint)Environment.ProcessId;
        var session = new ManagedSession
        {
            ProcessId = processId,
            ProcessStartedUtc = WindowInspector.GetProcessStartedUtc(processId)
        };

        Assert.IsTrue(WindowInspector.IsSameProcessInstance(processId, session));
    }

    [TestMethod]
    public void ReusedProcessIdIsRejected()
    {
        var processId = (uint)Environment.ProcessId;
        var session = new ManagedSession
        {
            ProcessId = processId,
            ProcessStartedUtc = DateTime.UtcNow.AddYears(-1)
        };

        Assert.IsFalse(WindowInspector.IsSameProcessInstance(processId, session));
    }

    private static NativeMethods.Rect Rect(int left, int top, int right, int bottom) => new()
    {
        Left = left,
        Top = top,
        Right = right,
        Bottom = bottom
    };
}
