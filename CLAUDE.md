# CLAUDE.md — Conversia

> Nombre de trabajo: **Conversia** (conversación + conversión). Provisional hasta validar marca y dominio.

## Qué es este proyecto

Plataforma SaaS para PyMEs que **captura y analiza las conversaciones comerciales de WhatsApp** entre asesores y clientes, y convierte esa información en métricas y tableros ejecutivos para tomar decisiones comerciales.

Los asesores **siguen respondiendo manualmente**; el sistema lee, clasifica, guarda y analiza los mensajes en segundo plano. No es (todavía) un chatbot que responda por los asesores.

Alianza: Daniel Escobar (arquitectura comercial y ventas, levanta las necesidades del cliente) + Daniel Restrepo (desarrollo de software y soluciones técnicas).

## Problema que resuelve

- Baja conversión en los embudos de venta sin saber por qué.
- Decisiones sin datos precisos; se culpa a los asesores sin evidencia.
- Alta rotación de personal comercial: el conocimiento se pierde con cada salida.

**Cliente piloto:** empresa de depilación láser con 4 sucursales que invierte en redes sociales pero no sabe por qué los clientes no cierran la compra.

## Principios de producto

1. **"Zapatero a tus zapatos":** resolver primero el problema concreto del cliente piloto, luego generalizar. No construir funciones "por si acaso".
2. **Dashboards ejecutivos simples:** un gerente debe entender la situación y "apagar un incendio" en segundos. Pocas métricas, claras y accionables. Si un reporte no se usa, sobra.
3. **No cargar a los asesores:** la captura de datos (edad, género, motivo de interés, origen, objeción) debe ser automática o casi automática.
4. **Confianza y cumplimiento primero:** privacidad, habeas data y políticas de WhatsApp no son opcionales.

## Alcance del MVP

1. **Ingesta:** recibir mensajes entrantes y salientes de WhatsApp vía webhook del proveedor y guardarlos.
2. **Modelo de datos:** empresas (multi-tenant), sucursales, asesores, contactos, conversaciones, mensajes, oportunidades y su estado en el embudo.
3. **Extracción y clasificación:** a partir de las conversaciones, identificar datos del cliente, motivo de interés, servicio consultado, objeciones y resultado (cerró / no cerró / en seguimiento).
4. **Métricas base:** tasa de conversión por sucursal, asesor y canal de origen; tiempo de primera respuesta; principales motivos de no cierre; volumen por día y hora.
5. **Dashboard ejecutivo:** una vista principal con 4–6 indicadores y alertas.

Fuera del MVP: respuestas automáticas del bot, conteo de personas o análisis de afluencia por cámara, integraciones con CRM/ERP externos.

## Stack

- **Backend:** .NET 10 (LTS), C#, ASP.NET Core Web API. La base del repo es la plantilla `DR.Template` 1.0.0, adaptada de SQL Server a PostgreSQL (ver `docs/decisiones.md`).
- **Base de datos:** PostgreSQL (se descartó SQL Server por costos). ORM: EF Core con Npgsql.
- **WhatsApp:** 360dialog como proveedor de la API de WhatsApp Business (por validar).
- **Infraestructura:** Azure.
- **Frontend del dashboard:** Blazor Web (de la plantilla DR.Template).
- **Análisis de texto / IA:** por definir (clasificación de intención, extracción de entidades).

## Arquitectura propuesta (inicial, abierta a cambios)

```
WhatsApp ─► 360dialog ─► Webhook (API .NET) ─► Cola / procesamiento en segundo plano
                                     │                     │
                                     ▼                     ▼
                               PostgreSQL ◄──── Extracción y clasificación
                                     │
                                     ▼
                           API de métricas ─► Dashboard ejecutivo
```

- El webhook debe responder rápido y delegar el procesamiento (no bloquear la recepción).
- Procesamiento idempotente: los proveedores pueden reenviar el mismo evento.
- Multi-tenant desde el inicio: todo registro pertenece a una empresa (`tenant_id`).

## Convenciones de código

- Código, nombres de clases, métodos y variables en **inglés**; comentarios y documentación de negocio pueden ir en español.
- Estructura sugerida: `src/` (proyectos de la solución), `tests/`, `docs/`.
- Separar dominio, aplicación, infraestructura y API (arquitectura limpia, sin sobreingeniería).
- Configuración y secretos por variables de entorno o Azure Key Vault. **Nunca** credenciales, tokens ni números reales en el repositorio.
- Migraciones de base de datos con EF Core, versionadas en el repo.
- Pruebas unitarias para la lógica de clasificación y métricas; pruebas de integración para el webhook.

## Privacidad, cumplimiento y riesgos

- **Ley 1581 de 2012 (habeas data, Colombia):** se tratan datos personales de clientes finales. Se requiere autorización, finalidad clara, política de tratamiento y mecanismos de consulta y supresión.
- Minimizar datos: guardar solo lo necesario para las métricas. Evaluar anonimizar o seudonimizar números telefónicos en la capa analítica.
- Cifrado en tránsito y en reposo. Control de acceso por empresa y rol.
- **Políticas de WhatsApp/Meta:** evitar prácticas que provoquen bloqueo de números. Validar con el proveedor cómo leer conversaciones atendidas manualmente sin romper las políticas (por ejemplo, si el número puede seguir usando la app de WhatsApp Business mientras se conecta a la API).

## Preguntas abiertas

- ¿Cómo se capturan los mensajes que los asesores envían desde su teléfono o WhatsApp Web? (define la viabilidad técnica de todo el producto).
- Tecnología del dashboard: Blazor Web viene con la plantilla DR.Template; falta confirmar si basta para el dashboard ejecutivo (React, Power BI embebido, otra).
- Motor de clasificación: reglas, modelo de IA vía API, o combinación.
- Nombre definitivo del producto.

## Cómo trabajar en este repo (instrucciones para Claude)

- Antes de implementar algo grande, propón un plan breve y espera confirmación.
- Iteraciones pequeñas: cada cambio debe compilar y pasar las pruebas.
- Si una decisión no está en este archivo, pregunta en vez de asumir; cuando se decida, propone actualizar este archivo o `docs/decisiones.md`.
- Prioriza siempre lo que necesita el cliente piloto para el MVP.
- Notas de reuniones, backlog y decisiones detalladas van en `docs/`, no aquí.

## Estructura actual del repo

```
Conversia.slnx
global.json, .editorconfig, .config/dotnet-tools.json   # dotnet-ef 10.0.11 como herramienta local
docker-compose.yml, docker-compose.demo.yml, .env.example, .dockerignore
.github/workflows/tests.yml
README.md                    # de la plantilla DR.Template
docs/
  decisiones.md              # registro de decisiones del proyecto
  ARCHITECTURE.md, EXTENDING.md, WORKFLOW.md, DEMO.md, ERRORS.md, USERS_TENANTS_PERMISSIONS.md   # de la plantilla
  WEBHOOKS.md                # ingesta del webhook de 360dialog (endpoint, mapeo, reintentos, cierre de conversaciones, formato no verificado)
  CLASSIFICATION.md          # clasificador de conversaciones (reglas en español; cómo conectar un LLM)
src/
  Conversia.Domain/          # sin dependencias
  Conversia.Application/     # referencia Domain
  Conversia.Infrastructure/  # referencia Application; EF Core + Npgsql, migraciones en Persistence/Migrations
  Conversia.API/             # controllers; referencia Application e Infrastructure
  Conversia.Web/             # Blazor Web; referencia Application y habla con la API por HTTP
tests/
  Conversia.Domain.UnitTests/, Conversia.Application.UnitTests/, Conversia.Infrastructure.UnitTests/   # xUnit
  Conversia.IntegrationTests/      # API contra PostgreSQL real (Testcontainers; requiere Docker)
  Conversia.Web.IntegrationTests/  # Web contra la API y PostgreSQL reales (Testcontainers)
```

Comandos habituales:

```bash
cp .env.example .env                      # completar POSTGRES_PASSWORD y JWT_SIGNING_KEY
docker compose up -d --build              # PostgreSQL + migraciones + API (5030) + Web (5112)
docker compose up -d postgres             # solo PostgreSQL, para desarrollar con dotnet run
dotnet tool restore                       # una vez: dotnet-ef
dotnet build
dotnet test                               # las pruebas de integración necesitan Docker
dotnet run --project src/Conversia.API    # OpenAPI en /openapi/v1.json (Development)
```
