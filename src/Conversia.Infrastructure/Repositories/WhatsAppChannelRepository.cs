using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Repositories;

// Every query is scoped to the caller's tenant by the global query filter on WhatsAppChannels.
public class WhatsAppChannelRepository : IWhatsAppChannelRepository
{
    private readonly ApplicationDbContext _dbContext;

    public WhatsAppChannelRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<WhatsAppChannel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _dbContext.WhatsAppChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<IReadOnlyList<WhatsAppChannel>> ListAsync(CancellationToken cancellationToken = default)
        => await _dbContext.WhatsAppChannels.AsNoTracking().OrderBy(c => c.DisplayPhoneNumber).ThenBy(c => c.Id).ToListAsync(cancellationToken);

    public Task<WhatsAppChannel?> GetByChannelKeyAsync(string channelKey, CancellationToken cancellationToken = default)
        => _dbContext.WhatsAppChannels.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.ChannelKey == channelKey, cancellationToken);

    public Task<WhatsAppChannel?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default)
        => _dbContext.WhatsAppChannels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task UpdateAsync(WhatsAppChannel channel, CancellationToken cancellationToken = default)
        => _dbContext.SaveChangesAsync(cancellationToken);

    public async Task AddAsync(WhatsAppChannel channel, CancellationToken cancellationToken = default)
    {
        await _dbContext.WhatsAppChannels.AddAsync(channel, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
