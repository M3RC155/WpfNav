namespace WpfNav.Tests.TestSupport;

/// <summary>Polls a condition every 25 ms and fails the test when it is still false after the timeout.</summary>
public static class Eventually
{
    public static Task True(Func<bool> condition, int timeoutMs) => TrueAsync(() => Task.FromResult(condition()), timeoutMs);

    public static async Task TrueAsync(Func<Task<bool>> condition, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) Assert.Fail($"condition not met within {timeoutMs} ms");
            await Task.Delay(25);
        }
    }
}
