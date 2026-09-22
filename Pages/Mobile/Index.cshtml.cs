using ebs50_backend.Data;
using ebs50_backend.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace ebs50_backend.Pages.Mobile;

/// <summary>
/// Lightweight PageModel for the mobile handheld dashboard.
/// Preloads initial model and state catalogs for instant mobile filter dropdowns.
/// </summary>
public class IndexModel(AppDbContext context, ILogger<IndexModel> logger) : PageModel
{
    public IReadOnlyList<ModelItem> Models { get; private set; } = [];
    public IReadOnlyList<MachineState> States { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Loading mobile handheld view catalogs...");

        States = await context.MachineStates
            .AsNoTracking()
            .OrderBy(s => s.StateCode)
            .ToListAsync(cancellationToken);

        Models = await context.ModelItems
            .AsNoTracking()
            .OrderBy(m => m.ModelCode)
            .ToListAsync(cancellationToken);
    }
}
