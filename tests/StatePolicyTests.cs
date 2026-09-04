using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FullScreenManager.Tests;

[TestClass]
public sealed class StatePolicyTests
{
    [TestMethod]
    [DataRow(true, false, true, true, DisplayName = "Fullscreen window is discovered")]
    [DataRow(false, false, true, false, DisplayName = "Windowed window is ignored")]
    [DataRow(true, true, true, false, DisplayName = "Managed window is ignored")]
    [DataRow(true, false, false, false, DisplayName = "Retry backoff is respected")]
    public void DiscoveryPolicy(bool fullscreen, bool managed, bool retryReady, bool expected) =>
        Assert.AreEqual(expected, StatePolicy.ShouldDiscover(fullscreen, managed, retryReady));

    [TestMethod]
    [DataRow(true, true, true, DisplayName = "Foreground launch follows user")]
    [DataRow(false, true, false, DisplayName = "Background launch does not steal focus")]
    [DataRow(true, false, false, DisplayName = "Inactive Space does not steal focus")]
    public void EvacuatedWindowPolicy(bool wasForeground, bool sourceCurrent, bool expected) =>
        Assert.AreEqual(expected, StatePolicy.ShouldFollowEvacuatedWindow(wasForeground, sourceCurrent));

    [TestMethod]
    public void CurrentMinimizedOwnerIsCleaned() => AssertDecision(
        Observation(iconic: true, current: true), SessionObservationAction.Cleanup);

    [TestMethod]
    public void ExclusiveTransitionMinimizeIsRetained() => AssertDecision(
        Observation(iconic: true, current: true, awaiting: true), SessionObservationAction.Keep);

    [TestMethod]
    public void BackgroundExclusiveMinimizeIsRetained() => AssertDecision(
        Observation(iconic: true), SessionObservationAction.Keep);

    [TestMethod]
    public void ConfirmedFullscreenExitIsCleaned() => AssertDecision(
        Observation(fullscreen: false, windowed: true, current: true, foreground: true),
        SessionObservationAction.Cleanup);

    [TestMethod]
    public void TransientBackgroundModeChangeIsRetained() => AssertDecision(
        Observation(fullscreen: false, windowed: true), SessionObservationAction.Keep);

    [TestMethod]
    public void DeadProcessIsCleaned() => AssertDecision(
        Observation(exists: false, processAlive: false), SessionObservationAction.Cleanup);

    [TestMethod]
    public void SharedHostRequiresConfirmations() => AssertDecision(
        Observation(exists: false, sharedHost: true, missing: 2), SessionObservationAction.Keep);

    [TestMethod]
    public void ConfirmedMissingSharedHostIsCleaned() => AssertDecision(
        Observation(exists: false, sharedHost: true, missing: 3), SessionObservationAction.Cleanup);

    [TestMethod]
    public void LiveCurrentGameMayReplaceItsWindow() => AssertDecision(
        Observation(exists: false, current: true, missing: 3), SessionObservationAction.Keep);

    [TestMethod]
    public void LiveAbandonedGameSpaceIsCleaned() => AssertDecision(
        Observation(exists: false, current: false, missing: 3), SessionObservationAction.Cleanup);

    private static void AssertDecision(WindowObservation observation, SessionObservationAction expected) =>
        Assert.AreEqual(expected, StatePolicy.Decide(observation));

    private static WindowObservation Observation(bool exists = true, bool visible = true,
        bool iconic = false, bool fullscreen = true, bool windowed = false,
        bool current = false, bool foreground = false, bool awaiting = false,
        bool processAlive = true, bool sharedHost = false, int missing = 0) =>
        new(exists, visible, iconic, fullscreen, windowed, current, foreground,
            awaiting, processAlive, sharedHost, missing);
}
