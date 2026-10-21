namespace LinkForge.Persistence.Tests;

/// <summary>
/// 可手工推进的时间源。
/// </summary>
/// <remarks>
/// 用它而不是等真实时间：断言"按最后修改时间排序"必须能精确控制两次写入的先后，
/// 靠 <c>Thread.Sleep</c> 会让测试既慢又不稳定（LF-009 的截图测试已踩过时序坑）。
/// </remarks>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    /// <summary>创建时间源。</summary>
    /// <param name="start">起始时间。</param>
    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>把时间向前推进。</summary>
    /// <param name="delta">增量。</param>
    public void Advance(TimeSpan delta) => _now += delta;
}
