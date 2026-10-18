using Conversia.Domain.Common;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

public class Tenant : BaseEntity
{
    private const int MaxNameLength = 200;

    public string Name { get; private set; } = string.Empty;

    // Required by EF Core for materialization.
    private Tenant()
    {
    }

    public static Tenant Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("El nombre del espacio de trabajo es obligatorio.");
        }

        if (name.Length > MaxNameLength)
        {
            throw new DomainException($"El nombre del espacio de trabajo no puede superar los {MaxNameLength} caracteres.");
        }

        if (name.Contains('\0'))
        {
            throw new DomainException("El nombre del espacio de trabajo no puede contener caracteres nulos.");
        }

        return new Tenant
        {
            Name = name
        };
    }
}
