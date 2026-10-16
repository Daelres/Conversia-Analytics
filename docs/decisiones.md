# Registro de decisiones

Formato: fecha, decisión, razón. Las decisiones abiertas se listan al final.

| Fecha | Decisión | Razón |
| --- | --- | --- |
| 2026-10-16 | Usar .NET 10 (LTS) con C# y ASP.NET Core Web API. | Es la versión LTS vigente; ofrece soporte a largo plazo y evita migraciones tempranas. |
| 2026-10-16 | PostgreSQL como base de datos, con EF Core y Npgsql. Se descarta SQL Server. | Menor costo de licenciamiento y operación; EF Core con Npgsql es una opción madura. |
| 2026-10-16 | Arquitectura limpia con cuatro proyectos en `src/`: Domain, Application, Infrastructure y Api. | Separa responsabilidades sin sobreingeniería y facilita las pruebas. |
| 2026-10-16 | xUnit como framework de pruebas, con proyectos separados para unitarias e integración. | Es el estándar del ecosistema .NET y se integra con `WebApplicationFactory`. |
| 2026-10-16 | Gestión central de paquetes (`Directory.Packages.props`). | Un único lugar para las versiones evita divergencias entre proyectos. |
| 2026-10-16 | El nombre "Conversia" es provisional. | Falta validar marca y dominio. |
| 2026-10-16 | Los secretos y la configuración sensible se manejan solo con variables de entorno o Azure Key Vault. | Nunca se versionan credenciales, tokens ni números reales en el repositorio. |

## Decisiones abiertas

- Tecnología del frontend del dashboard.
- Motor de clasificación y análisis con IA.
- Tecnología de cola y procesamiento en segundo plano.
