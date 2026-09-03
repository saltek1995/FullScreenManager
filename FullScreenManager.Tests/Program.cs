using FullScreenManager;

var tests = new (string Name, Action Run)[]
{
    ("startup discovers background fullscreen", () => AssertDiscovery(initial: true, foreground: false, expected: true)),
    ("runtime ignores background fullscreen", () => AssertDiscovery(initial: false, foreground: false, expected: false)),
    ("runtime recovers missed foreground fullscreen", () => AssertDiscovery(initial: false, foreground: true, expected: true)),
    ("suppressed auxiliary is ignored", () => AssertDiscovery(initial: true, foreground: true, expected: false, suppressed: true)),
    ("managed HWND is ignored", () => AssertDiscovery(initial: true, foreground: true, expected: false, managed: true)),
    ("retry backoff is respected", () => AssertDiscovery(initial: true, foreground: true, expected: false, retryReady: false)),
    ("foreground launch follows user", () => AssertFollow(wasForeground: true, sourceCurrent: true, expected: true)),
    ("background launch does not steal focus", () => AssertFollow(wasForeground: false, sourceCurrent: true, expected: false)),
    ("inactive managed Space does not steal focus", () => AssertFollow(wasForeground: true, sourceCurrent: false, expected: false)),
    ("current minimized owner is cleaned", () => AssertAction(Observation(iconic: true, current: true), SessionObservationAction.Cleanup)),
    ("exclusive transition minimize is retained", () => AssertAction(Observation(iconic: true, current: true, awaiting: true), SessionObservationAction.Keep)),
    ("background exclusive minimize is retained", () => AssertAction(Observation(iconic: true), SessionObservationAction.Keep)),
    ("confirmed fullscreen exit is cleaned", () => AssertAction(Observation(fullscreen: false, windowed: true, current: true, foreground: true), SessionObservationAction.Cleanup)),
    ("transient background mode change is retained", () => AssertAction(Observation(fullscreen: false, windowed: true), SessionObservationAction.Keep)),
    ("dead process is cleaned", () => AssertAction(Observation(exists: false, processAlive: false), SessionObservationAction.Cleanup)),
    ("shared host requires confirmations", () => AssertAction(Observation(exists: false, sharedHost: true, missing: 2), SessionObservationAction.Keep)),
    ("confirmed missing shared host is cleaned", () => AssertAction(Observation(exists: false, sharedHost: true, missing: 3), SessionObservationAction.Cleanup)),
    ("live current game may replace HWND", () => AssertAction(Observation(exists: false, current: true, missing: 3), SessionObservationAction.Keep)),
    ("live abandoned game Space is cleaned", () => AssertAction(Observation(exists: false, current: false, missing: 3), SessionObservationAction.Cleanup)),
    ("process instance identity matches", AssertCurrentProcessIdentity),
    ("reused PID identity is rejected", AssertReusedProcessIdentity)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed");
return failed == 0 ? 0 : 1;

static void AssertDiscovery(bool initial, bool foreground, bool expected,
    bool suppressed = false, bool managed = false, bool retryReady = true)
{
    var actual = StatePolicy.ShouldDiscover(true, suppressed, managed, initial, foreground, retryReady);
    if (actual != expected) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void AssertAction(WindowObservation observation, SessionObservationAction expected)
{
    var actual = StatePolicy.Decide(observation);
    if (actual != expected) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void AssertFollow(bool wasForeground, bool sourceCurrent, bool expected)
{
    var actual = StatePolicy.ShouldFollowEvacuatedWindow(wasForeground, sourceCurrent);
    if (actual != expected) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static WindowObservation Observation(bool exists = true, bool visible = true,
    bool iconic = false, bool fullscreen = true, bool windowed = false,
    bool current = false, bool foreground = false, bool awaiting = false,
    bool processAlive = true, bool sharedHost = false, int missing = 0) =>
    new(exists, visible, iconic, fullscreen, windowed, current, foreground,
        awaiting, processAlive, sharedHost, missing);

static void AssertCurrentProcessIdentity()
{
    var processId = (uint)Environment.ProcessId;
    var session = new ManagedSession
    {
        ProcessId = processId,
        ProcessStartedUtc = WindowInspector.GetProcessStartedUtc(processId)
    };
    if (!WindowInspector.IsSameProcessInstance(processId, session))
        throw new InvalidOperationException("Current process identity was rejected.");
}

static void AssertReusedProcessIdentity()
{
    var processId = (uint)Environment.ProcessId;
    var session = new ManagedSession
    {
        ProcessId = processId,
        ProcessStartedUtc = DateTime.UtcNow.AddYears(-1)
    };
    if (WindowInspector.IsSameProcessInstance(processId, session))
        throw new InvalidOperationException("A mismatched process start time was accepted.");
}
