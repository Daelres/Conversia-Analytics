# Configuración de 360dialog: sandbox y producción

Runbook para conectar Conversia con 360dialog. El procedimiento del **sandbox** se verificó de punta a punta (24-25 de octubre de 2026). El de **producción** está documentado a partir de la documentación oficial de 360dialog y **no se ha ejecutado todavía**.

Fuentes consultadas el 2026-10-24: https://docs.360dialog.com/docs/waba-messaging/sandbox/how-to-get-started-with-sandbox, https://docs.360dialog.com/docs/messaging-api/api-reference/webhooks.md, https://docs.360dialog.com/docs/messaging/webhook.md, https://docs.360dialog.com/docs/hub/webhook-url.md, https://docs.360dialog.com/docs/hub/api-key y https://docs.360dialog.com/docs/waba-management/api-key.

Marcadores: **[Verificado]** se ejecutó y funcionó; **[Documentado]** consta en la documentación de 360dialog, sin ejecutar; **[Por verificar]** no se ha comprobado.

Placeholders usados en todo el documento (nunca escribas valores reales en el repositorio):

| Placeholder | Qué es |
|---|---|
| `<D360_API_KEY>` | Llave de API de 360dialog (sandbox o producción) |
| `<CHANNEL_KEY>` | Última parte de la ruta `/api/webhooks/360dialog/<CHANNEL_KEY>`; la muestra Conversia al registrar la línea |
| `<WEBHOOK_SECRET>` | Secreto de la línea; Conversia lo muestra **una sola vez** |
| `<PUBLIC_API_HOST>` | Dirección pública HTTPS de la API de Conversia (túnel en sandbox, dominio propio en producción) |

---

## a) Sandbox frente a producción

| Aspecto | Sandbox | Producción |
|---|---|---|
| Servidor del endpoint de configuración | `https://waba-sandbox.360dialog.io` **[Verificado]** | `https://waba-v2.360dialog.io` (solo consta en la referencia de la API) **[Documentado]** |
| Endpoint | `POST /v1/configs/webhook` | `POST /v1/configs/webhook` (`GET` lee la configuración) |
| Cabecera de autenticación | `D360-API-KEY` | `D360-API-KEY` |
| Respuesta al registrar | `200` con `url` y `headers` en el cuerpo **[Verificado]** | `201` **[Documentado]** |
| Cómo se obtiene la llave | Enviar `START` (mayúsculas) desde tu WhatsApp al número `+551146733492` | Client Hub: Channels, canal, API Settings, menú de tres puntos, "Generate new API Key" |
| Alcance de la llave | Atada a un destinatario (tu teléfono); un nuevo `START` revoca la anterior | Una por número; generar otra invalida la anterior |
| Límite | 200 mensajes por llave; después `429` | Sin ese límite documentado |
| Qué mensajes llegan | Solo los enviados al número del sandbox, más estados de mensajes enviados por API | Los del número de negocio registrado. El formato real y los ecos de coexistencia están **[Por verificar]** |
| Dirección pública | Túnel temporal (`cloudflared`); cambia en cada arranque | Dirección estable con certificado TLS válido |
| Número de la línea en Conversia | El número del sandbox (no tu teléfono) | El número de negocio real |
| `providerPhoneNumberId` de la línea | Vacío | Recomendado completarlo **[Por verificar]**: de dónde se obtiene en 360dialog |
| Alta del número | No aplica | Requiere número de WhatsApp Business ya incorporado (Embedded Signup o Client Hub) |
| Cabeceras personalizadas (`headers`) | Aceptadas y reenviadas: `X-Webhook-Secret` funcionó **[Verificado]** | Documentadas en la referencia; reenvío **[Por verificar]** |

---

## b) Requisitos previos

Sandbox:

- [ ] Conversia levantada (`docker compose up -d --build`) y sana; ver [`PRUEBAS_SANDBOX.md`](PRUEBAS_SANDBOX.md), secciones 2 y 3.
- [ ] WhatsApp en tu teléfono para enviar `START` y los mensajes de prueba.
- [ ] `cloudflared` instalado (o similar), apuntando al puerto de la **API**, no al de la Web.
- [ ] `curl` y acceso como administrador de plataforma ([`PLATFORM_ADMIN.md`](PLATFORM_ADMIN.md)).

Producción:

- [ ] Número de WhatsApp Business incorporado en 360dialog (Embedded Signup o Client Hub) y con estado `DONE` en el canal. Para migrar un número desde otro proveedor, contacta al soporte de 360dialog **[Documentado]**.
- [ ] Quién gestiona la llave: con pago directo, el cliente la genera; si el número lo gestiona un partner, la crea el partner **[Documentado]**.
- [ ] Dirección pública HTTPS estable con certificado válido, **sin guiones bajos en el dominio y sin puerto explícito** **[Documentado]**. Hoy Azure con TLS está pendiente (ver [`PRUEBAS_SANDBOX.md`](PRUEBAS_SANDBOX.md), sección 10).
- [ ] La API de Conversia responde `200` en menos de 5 segundos. La aplicación ya responde rápido y procesa en segundo plano ([`WEBHOOKS.md`](WEBHOOKS.md)).
- [ ] Empresa activada por el administrador de plataforma con cupo de líneas suficiente.
- [ ] Autorización de tratamiento de datos (Ley 1581 de 2012) resuelta antes de conectar conversaciones reales.
- [ ] Gestor de secretos para la llave de 360dialog y el secreto de la línea (variables de entorno o Azure Key Vault).

---

## c) Procedimiento del sandbox **[Verificado]**

El orden importa: la línea debe existir y estar activa **antes** de que 360dialog entregue el primer mensaje.

| # | Acción | Resultado esperado |
|---|---|---|
| 1 | Como administrador de plataforma, activa la empresa y asigna cupo de líneas (`/admin`) | La empresa queda `Active` con cupo mayor a 0 |
| 2 | Como cliente, crea la sucursal (`/organizacion`) | La sucursal aparece en la lista |
| 3 | Registra la línea: número = **número del sandbox** (no tu teléfono); `providerPhoneNumberId` vacío | La línea se crea inactiva |
| 4 | Copia la **ruta** (`/api/webhooks/360dialog/<CHANNEL_KEY>`) y el **secreto** `<WEBHOOK_SECRET>` | Se muestran una sola vez; guárdalos en un gestor de secretos |
| 5 | Activa la línea | Estado activo |
| 6 | Obtén la llave: desde tu WhatsApp envía `START` (mayúsculas) a `+551146733492` | Respuesta con `<D360_API_KEY>`. Si no compartes tu número, la llave y un BSUID llegan en unos 5 minutos |
| 7 | Abre el túnel hacia la API: `cloudflared tunnel --url http://localhost:<API_PORT>` | Imprime una dirección `https://...trycloudflare.com` = `<PUBLIC_API_HOST>` |
| 8 | Prueba el túnel con un POST vacío (abajo) | `HTTP/2 200` |
| 9 | Registra el webhook en el sandbox (abajo) | `HTTP 200` con `url` y `headers` en el cuerpo |
| 10 | Envía un mensaje de texto desde tu teléfono al número del sandbox | El mensaje aparece en `/mensajes` |

Paso 8, prueba del túnel (el secreto se lee sin eco para que no quede en el historial):

```bash
read -rs WEBHOOK_SECRET
curl -i -X POST "https://<PUBLIC_API_HOST>/api/webhooks/360dialog/<CHANNEL_KEY>" \
  -H "X-Webhook-Secret: $WEBHOOK_SECRET" -H "Content-Type: application/json" -d '{}'
```

Paso 9, registro:

```bash
curl -X POST "https://waba-sandbox.360dialog.io/v1/configs/webhook" \
  -H "D360-API-KEY: $D360_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
        "url": "https://<PUBLIC_API_HOST>/api/webhooks/360dialog/<CHANNEL_KEY>",
        "headers": { "X-Webhook-Secret": "<WEBHOOK_SECRET>" }
      }'
```

Alternativa guiada: `bash scripts/sandbox-register-webhook.sh` hace los pasos 8 y 9 pidiendo los datos por terminal sin mostrarlos.

Notas del sandbox:

- La dirección del túnel **cambia en cada arranque**: repite el paso 9 cada vez.
- Solo llegan los mensajes **enviados al número del sandbox**; los salientes de asesores no se pueden validar aquí.
- Con la variante A (`X-Webhook-Secret`) el sandbox sí reenvió la cabecera; la variante B (`Authorization: Basic`) no fue necesaria.

---

## d) Procedimiento de producción **[Documentado, no ejecutado]**

| # | Acción | Resultado esperado |
|---|---|---|
| 1 | Verifica los requisitos de la sección b, en especial la dirección HTTPS estable y el canal en estado `DONE` | Lista de verificación completa |
| 2 | En Conversia: empresa activa, sucursal creada y línea registrada con el **número de negocio real**. Completa `providerPhoneNumberId` si lo conoces (**por verificar** de dónde sale); con un valor distinto al de la entrega, Conversia ignora esa entrega | Línea creada; ruta y secreto mostrados una vez |
| 3 | Guarda `<CHANNEL_KEY>` y `<WEBHOOK_SECRET>` en el gestor de secretos y activa la línea | Línea activa |
| 4 | Genera la llave: Client Hub, Channels, tu canal, API Settings, menú de tres puntos, "Generate new API Key", escribe `GENERATE`. El botón solo se habilita con el número en estado `DONE` | Se muestra `<D360_API_KEY>` **una sola vez**; cópiala al gestor de secretos. Generar otra invalida la anterior |
| 5 | Construye la URL: Conversia muestra solo la **ruta relativa**; antepón el host público. `https://<PUBLIC_API_HOST>/api/webhooks/360dialog/<CHANNEL_KEY>` | URL HTTPS, sin guiones bajos en el dominio ni puerto |
| 6 | Prueba la URL pública con un POST vacío y el secreto (igual que el paso 8 del sandbox) | `200` |
| 7 | Registra el webhook en producción (abajo) | `201` **[Documentado]** |
| 8 | Lee la configuración con `GET` para confirmarla | Devuelve la `url` registrada |
| 9 | Envía un mensaje real al número de negocio | Aparece en `/mensajes`, y la entrega en `/mensajes/eventos` |

Paso 7:

```bash
curl -X POST "https://waba-v2.360dialog.io/v1/configs/webhook" \
  -H "D360-API-KEY: $D360_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
        "url": "https://<PUBLIC_API_HOST>/api/webhooks/360dialog/<CHANNEL_KEY>",
        "headers": { "X-Webhook-Secret": "<WEBHOOK_SECRET>" }
      }'
```

Paso 8:

```bash
curl "https://waba-v2.360dialog.io/v1/configs/webhook" -H "D360-API-KEY: $D360_API_KEY"
```

Reglas de 360dialog para producción **[Documentado]**:

- Configura el webhook a nivel de **número de teléfono** (recomendado); a nivel de WABA solo si no hay uno por número.
- Debe responder `200` en **5 segundos**; si no, la entrega se considera fallida y se reintenta con espera exponencial hasta 7 días. Conversia es idempotente, así que los reenvíos no duplican mensajes.
- La URL también se puede fijar, actualizar o eliminar en el Hub ([`hub/webhook-url`](https://docs.360dialog.com/docs/hub/webhook-url.md)). Si usas el Hub, comprueba si permite cabeceras personalizadas **[Por verificar]**; si no, usa la API.
- Alternativa de autenticación: `Authorization: Basic base64(conversia:<WEBHOOK_SECRET>)` en `headers`; ver [`WEBHOOKS.md`](WEBHOOKS.md#authentication).

El script `scripts/sandbox-register-webhook.sh` **no sirve tal cual para producción**: la URL de 360dialog está escrita fija como `waba-sandbox.360dialog.io`, y trata `200` y `201` como éxito pero solo está probado contra el sandbox. Para producción usa los comandos de arriba, o una copia del script con otra URL, sin modificar el original. Tampoco acepta direcciones que no empiecen por `https://`, lo cual es correcto para producción.

---

## e) Rotar el secreto o cambiar la URL

Rotar el secreto de una línea (sandbox y producción):

1. En `/organizacion`, en la fila de la línea, pulsa **Rotar secreto** y confirma. El secreto anterior deja de funcionar **al instante**; la llave de la URL no cambia.
2. Copia el nuevo `<WEBHOOK_SECRET>` (se muestra una vez).
3. Vuelve a registrar el webhook con el nuevo secreto (mismo `POST /v1/configs/webhook`).

Resultado esperado: entre el paso 1 y el 3 las entregas reciben `401` y 360dialog las reintenta (hasta 7 días en producción); pasado el paso 3 llegan con normalidad. Haz la rotación en una ventana de bajo tráfico.

Cambiar la dirección pública (nuevo dominio, nuevo túnel):

1. Repite el `POST /v1/configs/webhook` con la nueva `url`. La ruta y la llave del canal no cambian.
2. Prueba con `GET` (solo producción) o enviando un mensaje.

Rotar la llave de 360dialog (producción): genera una nueva en el Hub (la anterior queda invalida), actualiza el gestor de secretos y cualquier proceso que la use.

---

## f) Problemas frecuentes

| Síntoma | Causa probable | Qué hacer |
|---|---|---|
| `400 url must be HTTPS` al registrar | La URL empieza por `http://` | Usa la dirección `https://` |
| `400` en producción | Dominio con guion bajo, puerto explícito o certificado no válido **[Documentado]** | Corrige la URL o el certificado |
| `401` o `403` de 360dialog al registrar | Llave errónea, revocada (nuevo `START` o nueva llave) o de otro entorno | Obtén la llave correcta; la de sandbox no sirve en producción |
| `429` de 360dialog en el sandbox | Se superaron 200 mensajes por llave | Solicita otra llave con `START` y vuelve a registrar el webhook con ella |
| `401` en Conversia con la prueba del paso 8 | Llave del canal o secreto incorrectos, línea inactiva o empresa no activa | Verifica ruta, secreto y estados. Ver [`PRUEBAS_SANDBOX.md`](PRUEBAS_SANDBOX.md), sección 6 |
| `429` en Conversia | Demasiados intentos fallidos desde la misma IP | Espera 60 segundos |
| Registro correcto pero no llegan mensajes | Túnel caído o con otra dirección; línea inactiva; mensaje enviado a un número que no es el del sandbox | Reabre el túnel, vuelve a registrar, revisa `/mensajes/eventos` |
| Llega la entrega pero el mensaje se ignora | `providerPhoneNumberId` de la línea distinto al de la entrega | Corrige o vacía ese campo |
| En producción las entregas fallan por tiempo | La API tarda más de 5 segundos | Revisa salud y carga de la API; el webhook debe responder y procesar en segundo plano |

---

## g) Pendiente de verificar en producción

- [ ] Que 360dialog en producción reenvíe `X-Webhook-Secret` (en el sandbox sí lo hizo).
- [ ] El formato real del mensaje de producción contra el mapeo de Conversia ([`WEBHOOKS.md`](WEBHOOKS.md#payload-mapping)).
- [ ] Los **ecos de coexistencia** (`smb_message_echoes`): los mensajes que el asesor envía desde la app del teléfono.
- [ ] De dónde se obtiene el `phone_number_id` para completar `providerPhoneNumberId`.
- [ ] Que `POST https://waba-v2.360dialog.io/v1/configs/webhook` responda `201`; ese servidor solo consta en la referencia de la API.
- [ ] Si el Hub permite cabeceras personalizadas al fijar la URL.
- [ ] Manejo de identificadores de usuario sin número de teléfono (BSUID).
- [ ] Dirección pública estable con TLS en Azure.
- [ ] Qué pasa con el historial previo del número al conectar (no se asume que se importe).
