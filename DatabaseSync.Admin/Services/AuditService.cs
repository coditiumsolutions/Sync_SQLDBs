using System.Security.Claims;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models.Management;

namespace DatabaseSync.Admin.Services;

public sealed class AuditService
{
    private readonly IManagementStore _store;

    public AuditService(IManagementStore store)
    {
        _store = store;
    }

    public Task WriteAsync(
        ClaimsPrincipal? user,
        string action,
        string category,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        long? userId = null;
        var idClaim = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (long.TryParse(idClaim, out var parsed))
        {
            userId = parsed;
        }

        return _store.WriteAuditAsync(new AuditEventRecord
        {
            UserId = userId,
            UserName = user?.Identity?.Name,
            Action = action,
            Category = category,
            Detail = detail,
            CreatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }
}
