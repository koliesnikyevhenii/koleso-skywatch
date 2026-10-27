namespace Skywatch.Worker.Tests.Fakes;

/// <summary>Waits for a background service to reach an observable state. Polls wall-clock time; the service's own timing uses FakeTimeProvider.</summary>
internal static class Eventually
{
    public static async Task ShouldHappen(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new ShouldAssertException($"Timed out waiting for: {what}");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
