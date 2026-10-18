# Registro de decisiones

Formato: fecha, decisión, razón. Las decisiones abiertas se listan al final.

| Fecha | Decisión | Razón |
| --- | --- | --- |
| 2026-10-16 | Usar .NET 10 (LTS) con C# y ASP.NET Core Web API. | Es la versión LTS vigente; ofrece soporte a largo plazo y evita migraciones tempranas. |
| 2026-10-16 | PostgreSQL como base de datos, con EF Core y Npgsql. Se descarta SQL Server. | Menor costo de licenciamiento y operación; EF Core con Npgsql es una opción madura. |
| 2026-10-16 | Arquitectura limpia con cuatro proyectos en `src/`: Domain, Application, Infrastructure y Api. **Reemplazada el 2026-10-18**: la plantilla trae cinco proyectos (se suma Web). | Separa responsabilidades sin sobreingeniería y facilita las pruebas. |
| 2026-10-16 | xUnit como framework de pruebas, con proyectos separados para unitarias e integración. **Ampliada el 2026-10-18**: la plantilla trae cinco proyectos de pruebas (unitarias de Domain, Application e Infrastructure; integración de API y de Web). | Es el estándar del ecosistema .NET y se integra con `WebApplicationFactory`. |
| 2026-10-16 | Gestión central de paquetes (`Directory.Packages.props`). **Reemplazada el 2026-10-18**: la plantilla fija las versiones en cada `.csproj`. | Un único lugar para las versiones evita divergencias entre proyectos. |
| 2026-10-16 | El nombre "Conversia" es provisional. | Falta validar marca y dominio. |
| 2026-10-16 | Los secretos y la configuración sensible se manejan solo con variables de entorno o Azure Key Vault. | Nunca se versionan credenciales, tokens ni números reales en el repositorio. |
| 2026-10-18 | La base del repositorio se genera con la plantilla `DR.Template` 1.0.0 (`dotnet new drtemplate -n Conversia`, feed NuGet privado), en lugar del andamiaje vacío hecho a mano. | Se parte de una base probada (usuarios, tenants, permisos, registro de errores, Docker, CI) en vez de reconstruirla. |
| 2026-10-18 | La plantilla se adaptó de SQL Server a PostgreSQL (EF Core con Npgsql 10, `postgres:17-alpine` en Docker, Testcontainers.PostgreSql en las pruebas) y se regeneró la migración como un único `InitialCreate`. Se mantiene la decisión del 2026-10-16. | La plantilla nace con SQL Server; el proyecto lo descartó por costos. Todo el stack de datos, las pruebas y los documentos pasan a PostgreSQL. |
| 2026-10-18 | El frontend es Blazor Web (viene con la plantilla en `src/Conversia.Web`). Sigue abierta la pregunta de si basta para el dashboard ejecutivo. | Es parte de la plantilla; se aprovecha mientras se valida la tecnología definitiva del dashboard. |
| 2026-10-18 | La API usa controllers (convención de la plantilla) en lugar de Minimal APIs. | Se sigue la convención de la plantilla para no divergir de su estructura, sus pruebas y su documentación. |
| 2026-10-18 | La concurrencia optimista de `Products` usa la columna de sistema `xmin` de PostgreSQL (propiedad `uint` marcada con `IsRowVersion()`). El contrato público no cambia: `rowVersion` sigue siendo un texto en base64 (ahora de 4 bytes) y una escritura desactualizada responde 409. | `rowversion` de SQL Server no existe en PostgreSQL; `xmin` es el mecanismo recomendado por Npgsql y no requiere columna ni trigger. |
| 2026-10-18 | La comparación sin distinguir mayúsculas se conserva con la extensión `citext` en `Users.Email` y `Products.Sku` (unicidad e inicio de sesión), y la búsqueda de productos usa `ILIKE` con los comodines escapados. | PostgreSQL distingue mayúsculas por defecto y SQL Server no. `citext` es la opción más simple y robusta: el índice único y las igualdades funcionan sin normalizar datos. Una collation no determinista no admite `LIKE`. |
| 2026-10-18 | La plantilla incluye una funcionalidad de referencia, Products, y los datos de demostración; se mantienen por ahora. Retirarlos se describe en `docs/EXTENDING.md`. | Sirven como ejemplo de la arquitectura; queda por decidir cuándo eliminarlos. |
| 2026-10-18 | Modelo de datos de la fase 1: `Branches`, `Advisors`, `WhatsAppChannels`, `Contacts`, `Conversations`, `Messages`, `Opportunities` y `WebhookEvents`. Cada tabla lleva su propio `TenantId` (también `Messages`, `Opportunities` y `WebhookEvents`) y el mismo filtro global de EF que `Products`. | Un filtro por tabla evita joins para aislar por empresa y permite que las métricas filtren directamente. Las referencias entre tablas se validan con consultas ya filtradas por empresa. |
| 2026-10-18 | Los enums del modelo de conversaciones (origen, estado, dirección, etapa, estado del evento) se guardan por nombre (`varchar`), no como entero. Los permisos siguen siendo enteros (convención de la plantilla). | Se leen directo en SQL y en herramientas de BI, y reordenar un enum no corrompe datos. |
| 2026-10-18 | Nuevos permisos `OrganizationView = 5` y `OrganizationManage = 6` para sucursales, asesores y canales. La API de organización solo expone crear y consultar (`POST`/`GET`); no hay edición ni baja todavía. | Alcance mínimo de la fase 1: lo necesario para dar de alta la estructura del cliente piloto. |
| 2026-10-18 | Una conversación abierta como máximo por contacto y canal (índice único parcial en la base de datos) y una sola oportunidad por conversación en la fase 1. | La regla de ingesta ("reutilizar la conversación abierta") queda garantizada por la base de datos y no solo por el código. Una conversación nunca se cierra por ahora: el cierre por inactividad queda para un slice posterior. |
| 2026-10-18 | Cada canal de WhatsApp tiene una `ChannelKey` aleatoria de 256 bits (va en la URL del webhook) y un secreto aleatorio que se muestra una sola vez al crearlo; solo se guarda su hash SHA-256. | La URL identifica el canal y la empresa sin exponer ids internos; el secreto autentica al proveedor sin quedar almacenado en claro. |

## Decisiones abiertas

- Tecnología del frontend del dashboard (por ahora Blazor Web, de la plantilla; ver 2026-10-18).
- Si se conserva o se elimina la funcionalidad de referencia Products y los datos de demostración.
- Motor de clasificación y análisis con IA.
- Tecnología de cola y procesamiento en segundo plano.
