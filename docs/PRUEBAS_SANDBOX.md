# Guía de pruebas con el sandbox de 360dialog

Esta guía te lleva, paso a paso, desde una instalación limpia hasta ver en Conversia los mensajes que escribes desde tu WhatsApp al número de pruebas (sandbox) de 360dialog. Al final hay una lista de aceptación para que decidas qué está bien y qué falta.

Convenciones: `<ENTRE_ANGULOS>` es un valor que debes reemplazar. Los comandos se ejecutan desde la carpeta raíz del repositorio. Cada paso dice **Qué debes ver** y, si no lo ves, **Si no ves eso**.

Estado de verificación de esta guía (22 de octubre de 2026): todo lo que ocurre dentro de Conversia (pasos 2, 3, 5 en parte, 7 y la lista de aceptación) se ejecutó contra una instalación real en Docker, emulando las entregas de 360dialog con cargas en formato Cloud API. Lo que depende de 360dialog, de tu teléfono o de un túnel público (pasos 1, 4 y el envío real del paso 5) **solo puedes ejecutarlo tú**; cada uno está marcado con **[Externo]**.

## 0. Qué vamos a probar y qué no

**Qué probamos**

- Que un mensaje que escribes desde tu WhatsApp al número del sandbox llega al webhook de Conversia, se guarda y se ve en la interfaz: `/mensajes` ("Mensajes recibidos"), `/mensajes/eventos` ("Entregas del webhook") y `/conversaciones`.
- Que el registro de la empresa, su activación por el administrador, el cupo de líneas y la suspensión funcionan como se describe.
- Que los mensajes repetidos no se duplican y que los tipos que no son texto (imagen, audio) se muestran con una etiqueta.

**Qué NO probamos**

- La capa de IA. La clasificación que ves (etapa, motivo, objeción) la hace un clasificador de reglas en español; no es parte de esta prueba y puede cambiar sola un minuto después del último mensaje.
- Las respuestas de los asesores. En el sandbox tu teléfono hace de **cliente** y el número del sandbox hace de **línea del negocio**. Según la documentación, solo se reenvían al webhook los mensajes que tú envías al sandbox; no está documentado que los mensajes que el sandbox te envía por su API se reflejen en el webhook (asume que no se reflejan). Por eso no se pueden validar aquí los mensajes salientes ("Saliente"), el tiempo de primera respuesta ni la regla de "ganada por confirmación del asesor".
- El formato real del mensaje que un asesor escribe desde la app WhatsApp Business (eco de coexistencia): sigue **sin verificar** (ver `docs/WEBHOOKS.md`).
- Clientes que WhatsApp identifica solo con un identificador de usuario (BSUID) y sin número de teléfono: Conversia los omite y los cuenta (ver el paso 6).

**Datos del sandbox que usamos** (fuente en cada fila; se contrastaron con la documentación pública el 22 de octubre de 2026 y pueden cambiar):

| Hecho | Fuente |
|---|---|
| La llave de prueba se obtiene enviando `START` (en mayúsculas) desde tu WhatsApp al número del sandbox; la respuesta trae la llave, atada a tu número. | https://docs.360dialog.com/docs/waba-messaging/sandbox/how-to-get-started-with-sandbox |
| Las páginas de 360dialog no coinciden en el número del sandbox (una lista `+551146733492`, otra `4930609859535`). **No lo fijes aquí**: usa el que muestre tu cuenta o la página actual. | https://docs.360dialog.com/docs/waba-messaging/sandbox/how-to-get-started-with-sandbox y https://docs.360dialog.com/partner/onboarding/sandbox |
| Cada llave está atada a un solo destinatario (tu teléfono). Volver a enviar `START` genera otra llave y revoca la anterior (dato aportado por el equipo del proyecto; la página citada no lo confirma: compruébalo cuando cambies de llave). | misma página del sandbox |
| Si no compartes tu número, WhatsApp pide el contacto y, pasado el tiempo de espera (hasta 5 minutos), te entrega la llave junto con un BSUID. | misma página del sandbox |
| Máximo de 200 mensajes por llave; pasado ese límite la API del sandbox responde `429`. | misma página del sandbox |
| El webhook se registra con `POST https://waba-sandbox.360dialog.io/v1/configs/webhook`, cabecera `D360-API-KEY` y un cuerpo JSON con `url`. La URL debe ser HTTPS (si no, `400` "url must be HTTPS"). | misma página del sandbox |
| La referencia de la API documenta que `POST /v1/configs/webhook` acepta además un objeto `headers` (valores de texto) que 360dialog reenvía con cada entrega. **No se verificó que el sandbox lo respete**: la página del sandbox no menciona cabeceras personalizadas y la referencia solo lista el servidor de producción. | https://docs.360dialog.com/docs/messaging-api/api-reference/webhooks.md y https://docs.360dialog.com/docs/messaging/webhook.md |

### Riesgo principal: la cabecera del secreto

Conversia autentica cada entrega con la cabecera `X-Webhook-Secret` (junto con la llave que va en la URL). **No hay otra forma de enviar el secreto**: el código solo lee esa cabecera (`WebhooksController`). Si el sandbox no reenvía cabeceras personalizadas, **todas las entregas recibirán `401` y no llegará ningún mensaje**, por mucho que todo lo demás esté bien. Cómo lo sabrás: en el paso 4 (Qué debes ver) la inspección del túnel muestra la petición de 360dialog sin esa cabecera y la respuesta `401`.

Si ocurre, es un bloqueo de código, no de configuración. Propuesta concreta (no incluida en esta tarea): aceptar el mismo secreto también en la cabecera `Authorization: Basic <base64(usuario:secreto)>` (360dialog documenta `Authorization` como cabecera personalizada del webhook). Cambia solo `WebhooksController` y `WebhookRoutes`; el resto de las reglas (comparación en tiempo constante, `401` uniforme) no cambia. No pongas el secreto en la URL: quedaría en los registros del túnel.

## 1. Requisitos externos

1. **Cuenta de 360dialog con acceso al sandbox.** Verifica en tu cuenta si el sandbox tiene costo; esta guía no lo asume.
2. **Tu WhatsApp** (el número que usarás como "cliente"). Usa **solo tu propio número** o el de personas que hayan dado su consentimiento. No uses datos de clientes reales: Conversia guarda el número, el nombre del perfil y el texto de cada mensaje (Ley 1581 de 2012).
3. **Un túnel HTTPS gratuito** hacia tu computador, porque 360dialog solo llama a direcciones públicas HTTPS. Elige uno:

   - **ngrok** (exige crear una cuenta gratuita y copiar tu token; verifica los límites del plan en su sitio). Tiene un inspector de peticiones que te ayudará mucho.
   - **cloudflared** (túnel rápido, sin cuenta; la dirección cambia en cada arranque y no hay garantía de disponibilidad). Fuente: https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/do-more-with-tunnels/trycloudflare/

   Para la primera prueba recomendamos **ngrok**: su inspector (`http://127.0.0.1:4040`) muestra si 360dialog llamó y qué respondió Conversia.

   ```bash
   brew install ngrok
   ```

   ```bash
   ngrok config add-authtoken <TU_TOKEN_DE_NGROK>
   ```

   ```bash
   brew install cloudflared
   ```
4. **Docker** con Compose v2, y una red estable: construir las imágenes descarga paquetes de internet.
5. **`curl`** (ya viene en macOS).

## 2. Preparar el entorno local

### Paso 2.1. Variables que debes agregar a tu `.env` a mano

Tu `.env` está protegido (no lo toca ninguna herramienta automática). Ya debe tener `POSTGRES_*` y `JWT_SIGNING_KEY`, y los puertos `POSTGRES_PORT`, `API_PORT` y `WEB_PORT` si los cambiaste (en tu instalación: API `15030`, Web `15112`, PostgreSQL `15432`). Falta el administrador de la plataforma. Esta línea agrega el correo y una contraseña aleatoria **sin mostrarla**:

```bash
printf '\nPLATFORM_ADMIN_EMAIL=%s\nPLATFORM_ADMIN_PASSWORD=%s\n' '<CORREO_DEL_ADMINISTRADOR>' "$(openssl rand -hex 16)" >> .env
```

Reglas: la contraseña debe tener al menos 12 caracteres (esta tiene 32) y no ser un valor de relleno. Si ya existen esas variables en el archivo, no repitas el comando (revisa con `rg -c '^PLATFORM_ADMIN_' .env`: debe dar `2`).

Para copiar la contraseña al portapapeles sin imprimirla (macOS):

```bash
rg -o '^PLATFORM_ADMIN_PASSWORD=(.*)' -r '$1' .env | pbcopy
```

Si prefieres leerla en pantalla: `rg '^PLATFORM_ADMIN_PASSWORD=' .env`. No la pegues en chats ni en documentos.

Opcional: un texto de contacto que verá una empresa pendiente o suspendida (una sola línea, hasta 300 caracteres):

```bash
printf 'SUPPORT_CONTACT_TEXT=%s\n' 'Escríbenos a <CORREO_DE_SOPORTE>' >> .env
```

Más adelante, cuando el administrador ya exista (paso 3.2), **borra la línea `PLATFORM_ADMIN_PASSWORD`** de `.env` y vuelve a crear el servicio `api` (`docker compose up -d api`): el contenedor conserva la variable mientras exista. Alternativa más segura: `PLATFORM_ADMIN_PASSWORD_FILE` con un secreto de Docker montado (ver `docs/PLATFORM_ADMIN.md`).

### Paso 2.2. Reconstruir con el código más reciente

```bash
docker compose up -d --build
```

**Qué debes ver:** la construcción termina sin errores y los contenedores quedan iniciados.

**Si no ves eso:**

- `port is already allocated`: otro programa usa ese puerto; cambia `POSTGRES_PORT`, `API_PORT` o `WEB_PORT` en `.env` y repite.
- Errores de red al construir (tiempos de espera descargando paquetes): repite el comando; si persiste, revisa tu conexión o VPN.
- `password authentication failed`: cambiaste `POSTGRES_PASSWORD` con un volumen ya creado (ver el paso 6).

### Paso 2.3. Comprobar que todo está sano

```bash
docker compose ps
```

**Qué debes ver:** `api`, `web` y `postgres` en estado `Up ... (healthy)`. (`docker compose ps -a` muestra además `migrator` como `Exited (0)`: es un trabajo que corre una vez y termina bien.)

```bash
curl -i http://localhost:<API_PORT>/health
```

**Qué debes ver:** `HTTP/1.1 200 OK` y el cuerpo `Healthy`. `Degraded` también responde 200 (hay eventos viejos o muchos fallos); `503 Unhealthy` significa que la API no llega a PostgreSQL.

**Si no ves eso:** `docker compose logs --tail 50 api`.

Para confirmar que el administrador se creó, busca esta línea en el registro de la API (no muestra correo ni contraseña):

```bash
docker compose logs api | rg 'Platform administrator created'
```

## 3. Registros en el sistema, en este orden

Abre la Web: `http://localhost:<WEB_PORT>` (en tu instalación, `http://localhost:15112`).

### 3.1. Crear la empresa (`/register`)

Entra a `/register` y llena:

| Campo | Qué es |
|---|---|
| Nombre de la empresa | El nombre con que verás la empresa. |
| Correo electrónico | Será la persona propietaria. Es único en todo el sistema. |
| Contraseña | De 12 a 128 caracteres, con mayúscula, minúscula y número. |

Pulsa **Registrarse**.

**Qué debes ver:** la página "Tu empresa está pendiente de activación", con el aviso "Tu empresa quedó registrada y está pendiente de activación." y el estado "Pendiente de activación". Si definiste `SUPPORT_CONTACT_TEXT`, también el recuadro "Cómo contactar al equipo". Mientras la empresa esté pendiente, cualquier otra página (`/mensajes`, `/organizacion`...) te devuelve a esa página y los enlaces del menú aparecen bloqueados. No puedes registrar números todavía.

**Si no ves eso:** "Ese correo ya está registrado." significa que el correo ya existe; usa otro.

### 3.2. Activar la empresa como administrador (`/admin/login`)

Cierra la sesión de la empresa (**Cerrar sesión**) o usa una ventana privada. Entra a `/admin/login` ("Administración de Conversia") con `PLATFORM_ADMIN_EMAIL` y `PLATFORM_ADMIN_PASSWORD` de tu `.env`. Este acceso es distinto del de las empresas y dura 60 minutos.

1. En `/admin` ("Empresas") verás un aviso con las empresas que esperan activación. Abre tu empresa.
2. Pulsa **Activar** y confirma con **Sí, activar** (el motivo es opcional; no escribas datos de clientes).
3. En la tarjeta "Cupo de líneas", escribe en "Cupo de líneas activas" un número **mayor o igual que 1** (las líneas que quieres tener encendidas a la vez) y pulsa **Guardar cupo**.

**Qué debes ver:** estado "Activa" y "Líneas activas 0 de 1" (o el cupo que fijaste). En "Cambios recientes" quedan registrados los dos cambios.

**Si no ves eso:** si `/admin/login` dice que no hay acceso, revisa que `PLATFORM_ADMIN_EMAIL`/`PLATFORM_ADMIN_PASSWORD` estén en `.env` y que recreaste `api` con `docker compose up -d`. Tras 5 intentos fallidos con el mismo correo, la API pide esperar (30 segundos y se duplica hasta 15 minutos). Si olvidaste la contraseña, no hay pantalla de recuperación: ver el runbook de `docs/PLATFORM_ADMIN.md`.

Después de este paso ya puedes quitar `PLATFORM_ADMIN_PASSWORD` de `.env` (paso 2.1).

### 3.3. Volver como cliente: sucursal (y asesor)

Cierra la sesión de administración, entra en `/login` con el correo y la contraseña de la empresa y abre **Organización** (`/organizacion`).

1. En "Nueva sucursal" escribe el nombre y pulsa **Crear sucursal**. Verás "Sucursal creada.".
2. Opcional: en "Asesores", escribe el nombre en "Nuevo asesor", elige la "Sucursal" y pulsa **Crear asesor** ("Asesor creado."). Un asesor no hace falta para esta prueba: las conversaciones quedan "Sin asignar".

### 3.4. Registrar la línea (el número del sandbox)

En "Números de WhatsApp" llena:

| Campo | Qué poner en el sandbox |
|---|---|
| Número de WhatsApp del negocio | **El número del sandbox de 360dialog** (la línea del negocio), con código de país; por ejemplo `+<CODIGO_PAIS><NUMERO_DEL_SANDBOX>`. **No pongas tu teléfono**: tu teléfono es el cliente. Acepta de 7 a 15 dígitos y los signos `+`, espacios, `-`, `(` y `)`. |
| Sucursal | La sucursal a la que pertenece la línea. |
| Identificador del número en el proveedor (opcional) | **Déjalo vacío.** Es el `phone_number_id` de 360dialog; si lo llenas, Conversia ignora las entregas que nombren otro número, y un valor equivocado haría que no veas nada. |

Pulsa **Registrar número**.

Qué hace cada campo en el sistema: el número es informativo (se muestra en las pantallas; Conversia **no** lo usa para encontrar la línea: la línea se encuentra por la llave de la URL del webhook). El identificador del proveedor, si existe, filtra las entregas. La sucursal decide a qué sucursal pertenecen las conversaciones de esa línea.

**Qué debes ver:** el panel "Número registrado: guarda su dirección y su secreto" con la "Ruta del webhook" y el "Secreto (cabecera X-Webhook-Secret)". La línea queda **Inactiva**.

**Si no ves eso:** un error de "Número de WhatsApp del negocio" indica que el formato no es válido.

### 3.5. Copiar la ruta y el secreto (se muestran UNA vez)

Antes de salir de esa pantalla, copia con **Copiar** la ruta y el secreto y guárdalos en un lugar privado (por ejemplo, un gestor de contraseñas). La ruta tiene esta forma: `/api/webhooks/360dialog/<CHANNEL_KEY>`. Conversia solo guarda una huella del secreto: no se puede volver a ver.

**Si lo perdiste:** en la fila de la línea pulsa **Rotar secreto** y confirma con **Sí, rotar el secreto**. Se muestra uno nuevo (una vez) y el anterior deja de funcionar al instante; la llave de la URL no cambia. Después actualiza la cabecera en 360dialog (paso 4).

### 3.6. Activar la línea

En la fila de la línea pulsa **Activar línea** y confirma con **Sí, activar la línea**.

**Qué debes ver:** "Línea activada: ya recibe mensajes." y "Líneas activas 1 de 1".

**Si no ves eso:** "Tu cupo de líneas activas está completo (X de X)..." significa que ya usas todo el cupo: desactiva otra línea o pide al administrador ampliarlo. Si la empresa no está activa, vuelve al paso 3.2.

## 4. Exponer la API con el túnel y registrar el webhook en 360dialog **[Externo]**

Este paso lo ejecutas tú: necesita internet, un túnel y tu llave del sandbox.

### Paso 4.1. Obtener la llave del sandbox

Desde **tu** WhatsApp, escribe `START` (mayúsculas) al número del sandbox que muestra tu cuenta o la página del sandbox de 360dialog. Comparte tu número si WhatsApp lo pide. Guarda la llave que te responden: está atada a tu teléfono. Si más tarde vuelves a enviar `START`, la llave anterior deja de servir.

### Paso 4.2. Abrir el túnel hacia la API (no hacia la Web)

Con ngrok:

```bash
ngrok http <API_PORT>
```

Con cloudflared:

```bash
cloudflared tunnel --url http://localhost:<API_PORT>
```

**Qué debes ver:** una dirección `https://<TUNNEL_HOST>` (`...ngrok...` o `....trycloudflare.com`). Déjala abierta: si cierras el túnel, la dirección cambia y tendrás que repetir el paso 4.4.

### Paso 4.3. Probar el túnel antes de involucrar a 360dialog

Guarda el secreto en una variable sin dejarlo en el historial:

```bash
read -rs WEBHOOK_SECRET && export WEBHOOK_SECRET
```

Envía una entrega vacía de prueba:

```bash
curl -i -X POST "https://<TUNNEL_HOST>/api/webhooks/360dialog/<CHANNEL_KEY>" -H "X-Webhook-Secret: $WEBHOOK_SECRET" -H "Content-Type: application/json" -d '{}'
```

**Qué debes ver:** `HTTP/1.1 200` con cuerpo vacío. En `/mensajes/eventos` aparece una entrega "Procesada" con resultado "Sin mensajes que guardar".

**Si no ves eso:** `401` = llave o secreto equivocados, línea inactiva o empresa que no está activa; `404` o error del túnel = la dirección del túnel o el puerto están mal (el túnel debe apuntar al puerto de la **API**); `429` = demasiados intentos fallidos desde tu red en un minuto; espera 60 segundos.

### Paso 4.4. Registrar el webhook en el sandbox

Guarda tu llave del sandbox sin mostrarla:

```bash
read -rs D360_API_KEY && export D360_API_KEY
```

Registra la dirección y el secreto:

```bash
curl -i -X POST "https://waba-sandbox.360dialog.io/v1/configs/webhook" -H "D360-API-KEY: $D360_API_KEY" -H "Content-Type: application/json" -d "{\"url\":\"https://<TUNNEL_HOST>/api/webhooks/360dialog/<CHANNEL_KEY>\",\"headers\":{\"X-Webhook-Secret\":\"$WEBHOOK_SECRET\"}}"
```

**Qué debes ver:** `HTTP/1.1 200` (según la documentación, el cuerpo repite la `url` registrada). Puedes cambiar la dirección en cualquier momento repitiendo el comando.

**Si no ves eso:** `400 "url must be HTTPS"` = usaste una dirección `http://`; `401` = la llave del sandbox es incorrecta o fue revocada por un nuevo `START`; una respuesta que rechace `headers` = **ver "Riesgo principal" en la sección 0**: en ese caso no podrás completar la prueba hasta cambiar el código.

## 5. Enviar mensajes desde tu WhatsApp y verificar **[Externo]** (la verificación en pantalla es la misma que ya se probó con entregas emuladas)

Abre `http://localhost:<WEB_PORT>/mensajes?auto=10` (se recarga solo cada 10 segundos) y, en otra pestaña, `/mensajes/eventos`.

Desde tu WhatsApp, escribe al número del sandbox, **dejando unos segundos entre uno y otro**:

| # | Qué envías | Qué debes ver en `/mensajes` |
|---|---|---|
| 1 | Un texto corto: `Hola, quiero información` | Un mensaje "Entrante" con ese texto, tu nombre de perfil y los últimos cuatro dígitos de tu número (`…1234`). |
| 2 | Un texto con emoji: `Me interesa 😀👍` | El mismo texto, con los emoji intactos. |
| 3 | Un texto largo (pega varios párrafos) | La tarjeta se corta a unas líneas; el texto completo está en la conversación ("Ver la conversación completa"). |
| 4 | Una imagen con pie de foto | El pie de foto como texto. |
| 5 | Una imagen sin pie de foto | `[Imagen sin texto]`. |
| 6 | Una nota de voz | `[Audio sin texto]`. |
| 7 | **El mismo texto otra vez** (por ejemplo, `Hola, quiero información` de nuevo) | Un mensaje nuevo: para WhatsApp son dos mensajes distintos. |

**Qué debes ver, en general:** cada mensaje aparece en pocos segundos. Arriba, "Mensajes hoy" y "Último mensaje entrante" se actualizan, y en la tarjeta "Líneas" aparece "Última entrega: ...". En `/mensajes/eventos` aparece una fila por cada aviso, con estado "Procesada" y resultado "Mensajes guardados". Pueden aparecer filas "Solo recibos de entrega" (avisos de enviado, entregado o leído) sin mensaje: es normal. En `/conversaciones` hay **una** conversación de tu número con todos los mensajes, "Sin asignar" y "Sin responder" (nadie le ha contestado desde Conversia: es lo esperado en el sandbox).

### Cómo leer `/mensajes`

- **Tarjetas de arriba:** mensajes de hoy (en la zona horaria del negocio, `America/Bogota` por defecto), último mensaje entrante y saliente, total de las últimas 24 horas.
- **"Líneas":** cada número, si está Activa o Inactiva y cuándo llegó la última entrega. "Nunca ha llegado una entrega a esta línea" significa que el proveedor jamás llamó al webhook de esa línea.
- **Cada mensaje:** la dirección (Entrante = el cliente; Saliente = el negocio), la hora a la que se envió y "llegó" a las que Conversia lo recibió, el cliente (nombre y últimos cuatro dígitos), la línea y la sucursal, y el texto.
- **Actualizar** recarga; **Actualizar solo: 5 s / 10 s / 30 s** recarga sola (`?auto=10`). **Marcar como vistos** pone la etiqueta "Nuevo" a lo que llegue después.
- Los filtros "Línea" y "Dirección" y el paginador funcionan con enlaces normales.

### Cómo leer `/mensajes/eventos`

Una fila por **aviso** de 360dialog (no por mensaje): hora de llegada, línea, estado (Pendiente, Procesada, Fallida), resultado, cuántos mensajes se guardaron, cuántos se omitieron, cuántos recibos y los intentos. El recuadro "Qué significa cada resultado" de la propia página explica cada uno:

| Resultado | Significa |
|---|---|
| Mensajes guardados | La entrega traía mensajes y ya están en "Mensajes recibidos". |
| Mensajes guardados, algunos omitidos | Se guardó lo que se pudo. |
| Solo recibos de entrega | Avisos de enviado, entregado o leído. No generan mensajes. |
| Mensajes omitidos | No se pudieron guardar (sin número de teléfono utilizable, sin identificador o fecha, de un grupo, de otro número). |
| Mensajes repetidos | Ya estaban guardados: el proveedor reenvió el mismo mensaje dentro de otra entrega. |
| Sin mensajes que guardar | Un aviso de otro tipo que el servicio no usa. |
| Pendiente de procesar / Pendiente de reintento / Falló | Cola, reintento automático y fallo definitivo (con el error, sin datos del cliente). |

Una **misma entrega idéntica** reenviada (mismo cuerpo exacto) no crea otra fila: 360dialog siempre recibe `200` y no se guarda nada nuevo. La fila "Mensajes repetidos" aparece solo cuando el mismo mensaje llega dentro de un cuerpo distinto.

**Si no ves nada:** pasa a la tabla del paso 6. La regla general: si no hay fila en `/mensajes/eventos`, **el aviso no llegó al webhook** (túnel, dirección, cabecera o llave); si hay fila pero no mensaje, el resultado dice por qué.

## 6. Problemas frecuentes

| Síntoma | Causa probable | Qué hacer |
|---|---|---|
| No llega nada: `/mensajes/eventos` vacío | El túnel se cerró o cambió de dirección | Abre el túnel de nuevo y repite el paso 4.4 con la dirección nueva. La dirección del túnel gratuito cambia en cada arranque. |
| No llega nada | URL o llave del canal mal copiadas | Compara la ruta registrada con la del paso 3.5. Una letra de más da `401`. |
| No llega nada | El webhook no está registrado, o la llave del sandbox fue revocada por un nuevo `START` | Repite el paso 4.4 con la llave vigente. |
| No llega nada, la inspección de ngrok muestra `401` | Línea inactiva | Actívala (paso 3.6). La tarjeta "Líneas" lo avisa: "está inactiva y el webhook la rechaza". |
| `401` | Empresa Pendiente o Suspendida | Pide al administrador activarla o reactivarla (paso 3.2). La respuesta es la misma que con un secreto equivocado, a propósito. |
| `401` | Falta la cabecera `X-Webhook-Secret` o el secreto es otro | Registra el webhook con `headers` (paso 4.4) o rota el secreto y registra el nuevo. Si el sandbox no reenvía la cabecera, ver "Riesgo principal". |
| `429` con `Retry-After` | Más de 60 intentos fallidos por minuto desde la misma dirección (o más de 6000 entregas por minuto en la línea) | Espera 60 segundos. Detrás de un túnel, todas las peticiones llegan desde la misma dirección. |
| Fila "Pendiente" que no avanza | Procesamiento detenido o empresa que dejó de estar activa | `docker compose logs --tail 50 api`; `curl http://localhost:<API_PORT>/health`. |
| Fila "Fallida" | Un error al procesar tras 8 intentos | Lee el error de la fila; ver `docs/WEBHOOKS.md` (Runbook) para reencolar. |
| Fila "Mensajes omitidos" | Remitente sin número de teléfono (identificador BSUID, p. ej. `CO.1349...`), grupo, fecha imposible, otro número | Comparte tu número con el sandbox. Conversia no guarda mensajes con un número inventado. Si tu llave se emitió con BSUID, repite `START` compartiendo tu número. |
| Mensajes que no aparecen aunque hay fila "Mensajes guardados" | Filtros activos en `/mensajes` | Pulsa "Borrar filtros". |
| `password authentication failed` al reconstruir | Cambiaste `POSTGRES_PASSWORD` con un volumen ya creado | Devuelve la contraseña anterior, cámbiala dentro de la base (ver `docs/DOCKER.md`) o recrea el volumen (paso 7: borra los datos). |
| `port is already allocated` | Otro programa usa el puerto | Cambia `POSTGRES_PORT`/`API_PORT`/`WEB_PORT` en `.env` y repite `docker compose up -d`. |
| Errores de red al construir imágenes | Conexión inestable | Repite `docker compose up -d --build`. |
| Los formularios fallan tras reiniciar | Sesiones abiertas antes de recrear el servicio web | Recarga la página e inicia sesión de nuevo. |
| Se acabaron los mensajes del sandbox | Límite de 200 por llave (`429` de la API del sandbox) | Pide otra llave con `START` (revoca la anterior). |

## 7. Reiniciar la prueba desde cero y limpiar datos de prueba

**Borrar solo los mensajes y conversaciones** (conserva empresa, sucursales, líneas y secretos). Esto borra los datos de **todas** las empresas de esta base de pruebas; usa solo en un entorno de pruebas:

```bash
docker compose exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "DELETE FROM \"Messages\"; DELETE FROM \"Opportunities\"; DELETE FROM \"Conversations\"; DELETE FROM \"Contacts\"; DELETE FROM \"WebhookEvents\";"'
```

**Empezar de cero por completo**, incluida la base de datos, las empresas y las claves de sesión de la Web: antes revisa qué volúmenes vas a borrar.

```bash
docker volume ls --filter name=conversia
```

```bash
docker compose down -v
```

`down -v` borra el volumen de **este** proyecto de Compose (base de datos y `web-keys`): todos tendrán que volver a registrarse y las líneas tendrán otro secreto y otra llave (hay que volver a registrar el webhook). No lo ejecutes si guardas datos que quieras conservar. Nunca lo ejecutes desde otra carpeta ni con otro nombre de proyecto.

Al empezar de cero, los datos de 360dialog (tu llave y tu número de sandbox) siguen intactos; solo cambia la dirección y el secreto que registras en el paso 4.

## 8. Privacidad y seguridad durante la prueba

- Usa solo tu número o el de personas que hayan consentido. No uses clientes reales.
- El túnel hace pública tu API. La llave de la URL (43 caracteres aleatorios) y el secreto son lo que protege el webhook; no compartas la dirección completa ni el secreto. Cierra el túnel cuando termines.
- Conversia guarda en cada entrega el número, el nombre de perfil y el texto de los mensajes. Las entregas procesadas se purgan a los 14 días y las fallidas a los 60; los mensajes y conversaciones **no** se purgan solos (decisión pendiente, ver la sección 10). Borra los datos de prueba al terminar (paso 7).
- El administrador de la plataforma **no** ve conversaciones ni mensajes de las empresas.
- La API se publica en todas las interfaces de tu computador (`API_PORT`), no solo en `localhost`; en una red compartida, ten presente que otros equipos podrían alcanzarla. PostgreSQL solo escucha en `127.0.0.1`.
- Nunca pegues llaves, secretos ni contraseñas en el repositorio, en `docs/` ni en conversaciones. `.env` está en `.gitignore`.

## 9. Lista de aceptación

Marca ☐ cuando lo hayas comprobado. La columna **Previo** indica qué ya se ejecutó antes de entregarte la guía: **V** = verificado contra una instalación real en Docker con entregas emuladas (22 de octubre de 2026); **E** = solo puedes comprobarlo tú (necesita 360dialog, tu teléfono o un túnel público).

| ID | Criterio | Cómo comprobarlo | Esperado | Previo | ☐ |
|---|---|---|---|---|---|
| CA-01 | La instalación está sana | `docker compose ps`; `curl -i http://localhost:<API_PORT>/health` | `api`, `web`, `postgres` healthy; `200 Healthy` | V | ☐ |
| CA-02 | Una empresa pendiente no puede operar | Registra una empresa y abre `/mensajes` | Te devuelve a "Tu empresa está pendiente de activación"; el menú aparece bloqueado | V | ☐ |
| CA-03 | El administrador activa y fija el cupo | Pasos 3.2 | "Activa" y "Líneas activas 0 de N" | V | ☐ |
| CA-04 | El cupo rechaza una línea activa de más | Registra una segunda línea y actívala con cupo 1 | "Tu cupo de líneas activas está completo (1 de 1)..." | V | ☐ |
| CA-05 | Una línea inactiva no recibe | Antes del paso 3.6, envía una entrega de prueba (paso 4.3) | `401` | V | ☐ |
| CA-06 | El webhook rechaza un secreto equivocado o ausente | Paso 4.3 con otro secreto y sin la cabecera | `401` en ambos casos, sin guardar nada | V | ☐ |
| CA-07 | El túnel HTTPS llega a la API | Paso 4.3 | `200` y una fila en `/mensajes/eventos` | E | ☐ |
| CA-08 | 360dialog acepta el webhook y reenvía la cabecera del secreto | Paso 4.4; luego el paso 5 | El registro responde 200 y las entregas no reciben `401` | E | ☐ |
| CA-09 | Un mensaje del sandbox aparece en `/mensajes` en pocos segundos | Paso 5, fila 1 | Mensaje "Entrante" con tu nombre y `…1234` | E (el camino interno: V) | ☐ |
| CA-10 | El texto con emoji y el texto largo llegan íntegros | Paso 5, filas 2 y 3 | Emoji intactos; el texto completo en la conversación | E (el camino interno: V) | ☐ |
| CA-11 | Un tipo que no es texto muestra una etiqueta | Paso 5, filas 5 y 6 | `[Imagen sin texto]`, `[Audio sin texto]` | E (el camino interno: V) | ☐ |
| CA-12 | Una entrega repetida no duplica el mensaje | Reenvía la misma entrega idéntica | `200` y sin mensaje nuevo; una fila nueva solo si el cuerpo cambia ("Mensajes repetidos") | V | ☐ |
| CA-13 | Cada entrega aparece con el resultado correcto | `/mensajes/eventos` | "Mensajes guardados", "Solo recibos de entrega", "Mensajes omitidos" según el caso | V | ☐ |
| CA-14 | La conversación aparece con su hilo | `/conversaciones` y su detalle | Una conversación del cliente con todos los mensajes en orden | V | ☐ |
| CA-15 | Una empresa suspendida deja de recibir y se reanuda | Suspende desde `/admin/empresas/<ID>`, envía; reactiva y envía | `401` mientras está suspendida y "Tu empresa está suspendida" en la Web; `200` al reactivar | V | ☐ |
| CA-16 | Los datos de cada empresa están aislados | Registra una segunda empresa, actívala | Su `/mensajes` está vacío ("Aún no llegan mensajes") | V | ☐ |
| CA-17 | Rotar el secreto invalida el anterior | "Rotar secreto" y envía con el viejo y el nuevo | El viejo da `401`; el nuevo `200` | V | ☐ |
| CA-18 | Entiendo lo que **no** cubre esta prueba | Sección 0 | Aceptado: sin IA, sin respuestas de asesores ni primera respuesta, eco de coexistencia sin verificar, BSUID omitidos | E | ☐ |

Si CA-07 a CA-11 pasan, el camino real 360dialog → túnel → Conversia → pantalla funciona.

## 10. Estado del producto y siguientes módulos

**Implementado y probado en esta versión**

- Ingesta por webhook de 360dialog: almacenamiento idempotente, procesamiento en segundo plano con reintentos, cierre automático de conversaciones inactivas.
- Modelo: empresas (multiempresa), sucursales, asesores, contactos, conversaciones, mensajes y oportunidades.
- Clasificador por reglas en español (servicio, motivo, objeción, resultado) y métricas definidas en `docs/METRICS.md`; tablero ejecutivo `/panel`.
- Administración de la plataforma: aprobación de empresas, cupo por línea, auditoría.
- Pantallas de verificación: `/mensajes`, `/mensajes/eventos`, `/conversaciones`, `/organizacion`.
- Simulador de webhooks (solo desarrollo) y despliegue con Docker.

**Pendiente o sin verificar**

- Adaptador de clasificación con un modelo de lenguaje (hoy solo reglas).
- Formato real de los mensajes que el asesor escribe desde la app (eco); sin él, la primera respuesta y la asignación por confirmación no están probadas con datos reales.
- Soporte de clientes identificados solo por BSUID (hoy se omiten).
- Retención de contactos, conversaciones y mensajes (plazos y supresión a petición del titular, Ley 1581 de 2012).
- La dirección pública de la API no es un ajuste: las pantallas muestran solo la ruta relativa del webhook.
- Administración de administradores (crear, listar, cambiar contraseña): hoy solo por arranque y base de datos.
- Contacto de soporte por empresa (hoy es un único texto para toda la instalación).
- Asignación automática de asesores y ventana de reapertura de conversaciones cerradas.
- Canales de alerta (correo, WhatsApp...): hoy las alertas solo se ven en el tablero.
- Facturación automática por línea (hoy solo el cupo), despliegue en Azure con TLS y copias de seguridad.

**Información que necesitamos de ti para especificar los siguientes módulos**

1. Catálogo real de servicios del piloto y el vocabulario de objeciones (cómo escriben los clientes).
2. Sucursales, asesores y cómo se reparten las conversaciones (¿por línea, por turno, por asesor?).
3. Horarios de atención y el tiempo máximo aceptable de primera respuesta (SLA) por sucursal.
4. Proveedor de modelo de lenguaje y decisión sobre protección de datos al enviarle conversaciones.
5. Reglas de facturación por línea y quién administra la plataforma.
6. Canales de notificación y qué alertas deben llegar a quién.
7. Roles y permisos que necesita cada tipo de usuario.
8. Plazo de conservación de conversaciones y proceso para atender solicitudes de supresión.
