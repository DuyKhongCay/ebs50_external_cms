using ebs50_backend.Data;
using ebs50_backend.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace ebs50_backend.Pages;

public class IndexModel(AppDbContext context, ILogger<IndexModel> logger) : PageModel
{
    public IList<EslTag> Tags { get; set; } = [];
    public IList<MachineState> States { get; set; } = [];
    public IList<ModelItem> Models { get; set; } = [];
    public int ModelCount => Models.Count;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Loading dashboard data...");
        States = await context.MachineStates
            .AsNoTracking()
            .OrderBy(s => s.StateCode)
            .ToListAsync(cancellationToken);

        Tags = await context.EslTags
            .AsNoTracking()
            .Include(t => t.CurrentState)
            .OrderBy(t => t.MachineNo)
            .ToListAsync(cancellationToken);

        Models = await context.ModelItems
            .AsNoTracking()
            .OrderBy(m => m.ModelCode)
            .ToListAsync(cancellationToken);
    }
}
