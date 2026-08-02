namespace AgentSplice.Application.Administration;

/// <summary>
/// Turns an administrative view into the bytes a client receives.
/// </summary>
/// <remarks>
/// A port for the same reason the protocol writers are: the API project must not decide what the
/// wire looks like, and an architecture test enforces that it does not reference
/// <c>System.Text.Json</c> at all. The application decides the payload; this decides its encoding.
/// </remarks>
public interface IAdministrativeEnvelopeWriter
{
    /// <summary>The media type every administrative response carries.</summary>
    string MediaType { get; }

    /// <summary>Writes a page of exchanges.</summary>
    ReadOnlyMemory<byte> Write(ExchangePageView page);

    /// <summary>Writes one exchange in full.</summary>
    ReadOnlyMemory<byte> Write(ExchangeDetailView detail);

    /// <summary>Writes a timeline.</summary>
    ReadOnlyMemory<byte> Write(IReadOnlyList<TimelineObservationView> observations);
}
