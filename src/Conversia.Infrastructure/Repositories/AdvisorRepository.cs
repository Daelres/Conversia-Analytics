using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Repositories;

// Every query is scoped to the caller's tenant by the global query filter on Advisors.
public class AdvisorRepository : IAdvisorRepository
{
    private readonly ApplicationDbContext _dbContext;

    public AdvisorRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Advisor?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _dbContext.Advisors.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Advisor>> ListAsync(CancellationToken cancellationToken = default)
        => await _dbContext.Advisors.AsNoTracking().OrderBy(a => a.Name).ThenBy(a => a.Id).ToListAsync(cancellationToken);

    public async Task AddAsync(Advisor advisor, CancellationToken cancellationToken = default)
    {
        await _dbContext.Advisors.AddAsync(advisor, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
