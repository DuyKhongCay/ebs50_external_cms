namespace ebs50_backend.Services.Rendering;

/// <summary>
/// Parameters required to render an ESL tag display image.
/// </summary>
public sealed record TagRenderRequest(
    string MacAddress,
    string ModelCode,
    int StateCode,
    string? MachineNo = null,
    string? OverrideThemeColor = null);

/// <summary>
/// Result containing generated image bytes and metadata.
/// </summary>
public sealed record TagRenderResult(
    byte[] ImageBytes,
    string ContentType,
    int Width,
    int Height);

/// <summary>
/// Contract for rendering 4-color 400x300 E-Paper images for Opticon ESL tags.
/// </summary>
public interface ITagRenderService
{
    /// <summary>
    /// Renders an E-paper image based on the specified model, state, and theme colors.
    /// </summary>
    /// <param name="request">Tag rendering parameters.</param>
    /// <param name="cancellationToken">Cancellation token to observe.</param>
    /// <returns>Rendered image result containing byte array and image dimensions.</returns>
    Task<TagRenderResult> RenderTagImageAsync(
        TagRenderRequest request,
        CancellationToken cancellationToken = default);
}

