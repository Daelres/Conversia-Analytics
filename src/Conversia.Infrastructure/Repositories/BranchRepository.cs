using Conversia.Application.Exceptions;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Repositories;

// Every query is scoped to the caller's tenant by the global query filter on Branches.
public class BranchRepository : IBranchRepository
{
    private readonly ApplicationDbContext _dbContext;

    public BranchRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Branch?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _dbContext.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Branch>> ListAsync(CancellationToken cancellationToken = default)
        => await _dbContext.Branches.AsNoTracking().OrderBy(b => b.Name).ThenBy(b => b.Id).ToListAsync(cancellationToken);

    public async Task AddAsync(Branch branch, CancellationToken cancellationToken = default)
    {
        await _dbContext.Branches.AddAsync(branch, cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new DuplicateBranchNameException();
        }
    }
}
