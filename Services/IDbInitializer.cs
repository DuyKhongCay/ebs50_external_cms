namespace ebs50_backend.Services;

/// <summary>
/// Service contract for asynchronous database initialization and legacy seeding.
/// </summary>
public interface IDbInitializer
{
    /// <summary>
    /// Ensures database creation and populates initial default states and legacy data.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to observe.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

