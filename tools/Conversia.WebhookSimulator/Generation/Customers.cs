namespace Conversia.WebhookSimulator.Generation;

/// <summary>Invented customers: first names and surnames common in Colombia and numbers in the +57 300 555 range the repository's samples already use.</summary>
public static class Customers
{
    private static readonly string[] FirstNames =
    [
        "Laura", "Camila", "Valentina", "Daniela", "Sofía", "Mariana", "Paula", "Natalia", "Carolina", "Juliana", "Andrea", "Luisa", "Manuela", "Isabela", "Alejandra",
        "Catalina", "Gabriela", "Stephanie", "Verónica", "Diana", "Lina", "Karen", "Tatiana", "Melissa", "Johana", "Susana", "Marcela", "Viviana", "Ángela", "Yuliana",
        "Santiago", "Andrés", "Felipe", "Mateo", "Sebastián", "Camilo", "Daniel", "Juan",
    ];

    private static readonly string[] Surnames =
    [
        "Gómez", "Rodríguez", "Martínez", "López", "García", "Hernández", "Ramírez", "Torres", "Vargas", "Castro", "Ríos", "Moreno", "Jiménez", "Ortiz", "Rojas",
        "Londoño", "Restrepo", "Zapata", "Arango", "Cardona", "Osorio", "Giraldo", "Mejía", "Salazar",
    ];

    public static string FirstName(Random random) => FirstNames[random.Next(FirstNames.Length)];

    /// <summary>A profile name as WhatsApp shows it: a first name, sometimes with the initial of a surname.</summary>
    public static string ProfileName(Random random, string firstName)
        => random.Next(100) < 45 ? firstName : $"{firstName} {Surnames[random.Next(Surnames.Length)]}";

    /// <summary>A number nobody owns on purpose (+57 300 555 xxxx), unique within <paramref name="taken"/>.</summary>
    public static string WaId(Random random, ISet<string> taken)
    {
        string number;
        do
        {
            number = "57300555" + random.Next(0, 10_000).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
        }
        while (!taken.Add(number));

        return number;
    }
}
