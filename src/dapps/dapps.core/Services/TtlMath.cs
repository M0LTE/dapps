namespace dapps.core.Services;

/// <summary>
/// Pure helpers for the residual-TTL arithmetic. Decoupled from any I/O so
/// the decrement and expiry rules can be unit tested without standing up
/// the database or transport.
///
/// Convention: <c>ttl</c> is residual lifetime in whole seconds (per the
/// on-air protocol). <c>createdAt</c> is the UTC instant the row entered
/// our queue. <c>now</c> is whatever clock the caller is using.
/// </summary>
public static class TtlMath
{
    /// <summary>
    /// Compute the residual ttl after time spent in our queue. Returns null
    /// if the row has no ttl (which means "no expiry"). Returns the
    /// remaining seconds otherwise - possibly zero or negative, in which
    /// case the caller MUST drop the row per the spec.
    /// </summary>
    public static int? Residual(int? ttl, DateTime createdAt, DateTime now)
    {
        if (ttl is null) return null;

        // Round elapsed up to the nearest whole second so we don't keep a
        // ttl-bearing row past its deadline by sub-second sloppiness.
        var elapsed = (long)Math.Ceiling((now - createdAt).TotalSeconds);
        var remaining = ttl.Value - elapsed;
        return remaining > int.MaxValue ? int.MaxValue
            : remaining < int.MinValue ? int.MinValue
            : (int)remaining;
    }

    /// <summary>True when a row with the given ttl/createdAt has reached or
    /// passed its deadline. Rows without a ttl never expire.</summary>
    public static bool HasExpired(int? ttl, DateTime createdAt, DateTime now)
    {
        var residual = Residual(ttl, createdAt, now);
        return residual is not null && residual.Value <= 0;
    }
}
