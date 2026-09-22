using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ebs50_backend.Pages;

/// <summary>Local maintenance UI. Database work is performed only by the background maintenance service.</summary>
public sealed class DatabaseModel : PageModel
{
    public void OnGet() { }
}
