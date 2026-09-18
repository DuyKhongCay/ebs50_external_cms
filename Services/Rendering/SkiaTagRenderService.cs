using System.Collections.Concurrent;
using ebs50_backend.Data;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace ebs50_backend.Services.Rendering;

/// <summary>
/// High-performance SkiaSharp rendering service for Opticon ESL tags (SE420RY 400x300 4-color E-Paper).
/// </summary>
internal sealed class SkiaTagRenderService : ITagRenderService, IDisposable
{
    public const int TargetWidth = 400;
    public const int TargetHeight = 300;

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<SkiaTagRenderService> _logger;
    private readonly AppDbContext _dbContext;

    // NOTE: Cached background templates (400x300) to avoid repetitive disk I/O and downscaling
    private readonly ConcurrentDictionary<int, SKBitmap> _templateCache = new();
    private SKTypeface? _boldTypeface;
    private bool _disposed;

    public SkiaTagRenderService(
        IWebHostEnvironment env,
        ILogger<SkiaTagRenderService> logger,
        AppDbContext dbContext)
    {
        _env = env;
        _logger = logger;
        _dbContext = dbContext;

        InitializeTypeface();
    }

    private void InitializeTypeface()
    {
        try
        {
            var fontPath = ResolveAssetPath("Fonts", "ARIALNB.TTF");
            if (!File.Exists(fontPath))
            {
                fontPath = ResolveAssetPath("Fonts", "arialbd.ttf");
            }

            if (File.Exists(fontPath))
            {
                _boldTypeface = SKTypeface.FromFile(fontPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load bundled font file. Defaulting to system typeface.");
        }

        _boldTypeface ??= SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
    }

    public async Task<TagRenderResult> RenderTagImageAsync(
        TagRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Fetch state details from DB if needed to determine theme color and icon file
        var stateColor = request.OverrideThemeColor;
        string? iconFileName = null;

        var state = await _dbContext.MachineStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.StateCode == request.StateCode, cancellationToken);

        if (state != null)
        {
            stateColor ??= state.ThemeColor;
            iconFileName = state.IconFileName;
        }

        stateColor ??= "Red";

        using var canvasBitmap = new SKBitmap(TargetWidth, TargetHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(canvasBitmap);
        canvas.Clear(EslColorPalette.White);

        var background = GetOrLoadTemplate(request.StateCode, iconFileName);
        if (background != null)
        {
            canvas.DrawBitmap(background, 0, 0);
        }
        else
        {
            DrawFallbackFrame(canvas, request.StateCode);
        }

        var textColor = EslColorPalette.ResolveColor(stateColor);
        DrawModelText(canvas, request.ModelCode, textColor);

        // NOTE: Quantize pixels to pure 4-color palette (White, Black, Red, Yellow) to eliminate anti-aliased gray/pink artifacts on e-paper
        using var quantizedBitmap = QuantizeTo4Colors(canvasBitmap);

        using var image = SKImage.FromBitmap(quantizedBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();

        return new TagRenderResult(bytes, "image/png", TargetWidth, TargetHeight);
    }

    private void DrawModelText(SKCanvas canvas, string modelCode, SKColor color)
    {
        if (string.IsNullOrWhiteSpace(modelCode)) return;

        // NOTE: Standard bounding box is centered at X: 200, baseline Y: 90 to prevent overlapping the "MODEL" header
        const float centerX = 200f;
        const float baselineY = 90f;
        const float maxBoxWidth = 320f;
        float fontSize = 42f;

        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Typeface = _boldTypeface,
            TextSize = fontSize,
            TextAlign = SKTextAlign.Center
        };

        float measuredWidth = paint.MeasureText(modelCode);
        while (measuredWidth > maxBoxWidth && fontSize > 18f)
        {
            fontSize -= 2f;
            paint.TextSize = fontSize;
            measuredWidth = paint.MeasureText(modelCode);
        }

        canvas.DrawText(modelCode, centerX, baselineY, paint);
    }

    private SKBitmap? GetOrLoadTemplate(int stateCode, string? iconFileName = null)
    {
        // If an explicit iconFileName is provided, invalidate cache if filename changed
        return _templateCache.GetOrAdd(stateCode, code =>
        {
            var fileName = !string.IsNullOrWhiteSpace(iconFileName)
                ? iconFileName
                : code switch
                {
                    0 => "running.png",
                    1 => "no_plan.png",
                    2 => "no_input.png",
                    3 => "broken.png",
                    4 => "change_model.png",
                    _ => $"state_{code}.png"
                };

            var filePath = ResolveAssetPath("Templates", fileName);
            if (!File.Exists(filePath))
            {
                // Fallback to running.png if specific icon is missing
                filePath = ResolveAssetPath("Templates", "running.png");
            }

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Template asset file not found at: {Path}", filePath);
                return null!;
            }

            try
            {
                using var original = SKBitmap.Decode(filePath);
                if (original == null) return null!;

                // Resize to exact 400x300 target resolution
                var resized = new SKBitmap(TargetWidth, TargetHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var c = new SKCanvas(resized))
                {
                    using var paint = new SKPaint
                    {
                        FilterQuality = SKFilterQuality.High,
                        IsAntialias = true
                    };
                    c.DrawBitmap(original, new SKRect(0, 0, TargetWidth, TargetHeight), paint);
                }

                return resized;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load and resize template asset: {Path}", filePath);
                return null!;
            }
        });
    }

    private static void DrawFallbackFrame(SKCanvas canvas, int stateCode)
    {
        using var strokePaint = new SKPaint
        {
            Color = EslColorPalette.Black,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            IsAntialias = true
        };

        canvas.DrawRoundRect(10, 8, 380, 95, 8, 8, strokePaint);
    }

    /// <summary>
    /// Snaps all pixels in the bitmap to one of the 4 valid E-Paper colors (White, Black, Red, Yellow).
    /// Prevents blurred anti-aliased gray or pink artifact pixels on monochrome/tri-color e-paper displays.
    /// </summary>
    private static SKBitmap QuantizeTo4Colors(SKBitmap source)
    {
        var result = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                var quantized = EslColorPalette.SnapToClosestPaletteColor(pixel);
                result.SetPixel(x, y, quantized);
            }
        }

        return result;
    }

    private string ResolveAssetPath(string subDir, string fileName)
    {
        var path = Path.Combine(_env.ContentRootPath, "Assets", subDir, fileName);
        if (File.Exists(path)) return path;

        // NOTE: Fallback to backup template directory during development if local Assets/ has not been populated
        var rootDir = Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
        var fallbackDir = subDir == "Templates" ? "DRT_finger_assets" : "TemplateFonts";
        var backupPath = Path.Combine(rootDir, "backup_20260915_100710", "Templates", fallbackDir, fileName);
        return backupPath;
    }

    public void Dispose()
    {
        if (_disposed) return;

        foreach (var bmp in _templateCache.Values)
        {
            bmp?.Dispose();
        }
        _templateCache.Clear();

        _boldTypeface?.Dispose();
        _disposed = true;
    }
}

