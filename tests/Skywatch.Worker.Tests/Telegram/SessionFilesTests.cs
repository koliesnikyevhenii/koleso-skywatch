using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Telegram;

// AC-F1.7 (one process per session) and AC-F1.8 (rejection marker): the files next to the session file.
public sealed class SessionFilesTests : IDisposable
{
    private readonly TestHost.TempFile _session = TestHost.ExistingSessionFile();

    public void Dispose() => _session.Dispose();

    // AC-F1.7: while one holder has the lock, a second attempt (another process, or a second open here) fails.
    [Fact]
    public void AC_F1_7_second_lock_on_the_same_session_fails_until_the_first_is_released()
    {
        using (var first = SessionFiles.TryLock(_session.Path))
        {
            first.ShouldNotBeNull();
            SessionFiles.TryLock(_session.Path).ShouldBeNull();
        }

        using var again = SessionFiles.TryLock(_session.Path);
        again.ShouldNotBeNull();
    }

    // AC-F1.7: the lock is a separate file next to the session; the session file itself stays readable.
    [Fact]
    public void AC_F1_7_lock_file_sits_next_to_the_session_and_leaves_it_readable()
    {
        using var held = SessionFiles.TryLock(_session.Path);

        SessionFiles.LockPath(_session.Path).ShouldBe(Path.GetFullPath(_session.Path) + ".lock");
        File.Exists(SessionFiles.LockPath(_session.Path)).ShouldBeTrue();
        Should.NotThrow(() => File.ReadAllBytes(_session.Path));
    }

    // AC-F1.8: the marker is written on rejection and removed by a successful login.
    [Fact]
    public void AC_F1_8_rejection_marker_round_trip()
    {
        SessionFiles.IsMarkedRejected(_session.Path).ShouldBeFalse();

        SessionFiles.MarkRejected(_session.Path, new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc));
        SessionFiles.IsMarkedRejected(_session.Path).ShouldBeTrue();
        File.ReadAllText(SessionFiles.RejectedMarkerPath(_session.Path)).ShouldBe("2026-10-30T08:00:00.0000000Z");

        SessionFiles.ClearRejected(_session.Path);
        SessionFiles.IsMarkedRejected(_session.Path).ShouldBeFalse();
        Should.NotThrow(() => SessionFiles.ClearRejected(_session.Path));
    }
}
