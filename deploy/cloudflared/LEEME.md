# Cloudflare Tunnel para el SyncHub

El hub escucha solo en `http://localhost:5080`. `cloudflared` crea una conexión saliente hacia Cloudflare y publica el hub en `https://sync.<DOMINIO>` con TLS gestionado. No se abren puertos ni se necesita IP fija.

Requisitos del cliente:
- Una cuenta de Cloudflare (sirve el plan gratuito).
- Un dominio administrado en esa cuenta.
- Salida a internet por el puerto 443 desde el servidor del hub.

## Pasos (en el servidor del hub, PowerShell como administrador)

1. Instalar `cloudflared`: `winget install --id Cloudflare.cloudflared`
2. Autenticar la cuenta. Abre el navegador para elegir el dominio:
   `cloudflared tunnel login`
3. Crear el túnel. Anote el `TUNNEL_ID` que imprime:
   `cloudflared tunnel create sigmasync-hub`
4. Mover el archivo de credenciales `<TUNNEL_ID>.json`, que queda en `%USERPROFILE%\.cloudflared\`, a `C:\SigmaSync\cloudflared\`.
5. Copiar `config.yml.example` como `C:\SigmaSync\cloudflared\config.yml` y completar `<TUNNEL_ID>` y `<DOMINIO>`.
6. Crear el registro DNS:
   `cloudflared tunnel route dns sigmasync-hub sync.<DOMINIO>`
7. Instalar `cloudflared` como servicio Windows con esa configuración:
   `cloudflared --config C:\SigmaSync\cloudflared\config.yml service install`
8. Verificar desde otro equipo: `https://sync.<DOMINIO>/salud` debe responder `{"estado":"ok"}`.

En cada agente, `Agente:HubUrl` pasa a ser `https://sync.<DOMINIO>`. Los agentes de Fábrica y CEDI, que corren en el mismo servidor que el hub, pueden seguir usando `http://localhost:5080`.

> Pendiente de validar en el ambiente del cliente: estos pasos no se probaron todavía con la cuenta real.
