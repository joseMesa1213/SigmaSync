**Asunto:** Sincronización SAINT: desarrollo terminado, 4 condiciones del cliente para salir a producción

Hola Carlos,

Te cuento cómo vamos con la sincronización. El desarrollo está terminado, y para salir a producción necesitamos que el cliente resuelva cuatro puntos. El detalle está en `docs/diseno-tecnico.md` y en `notas.md`.

**1. Estado**
- Los tres flujos están terminados y probados de punta a punta, contra copias locales de las tres bases:
  - **F1:** OC del PV → pedido en Fábrica o CEDI.
  - **F2:** factura hecha desde ese pedido → OC en el PV, completa o parcial.
  - **F3:** factura sin pedido → OC en el PV.
- También está lista la operación: panel, alertas, reproceso y manual.
- Hay 72 pruebas automáticas, todas en verde.
- El 4 y el 5 de noviembre trabajé por AnyDesk en el servidor de Fábrica y CEDI y en la máquina del PV Mayorista. Hice las pruebas en pantalla sobre **copias** de las bases, con un acceso de SAINT apuntando a la copia. **No se escribió nada en producción.**
  - Grabé pedidos, compras, OC y facturas completas y parciales en los tres nodos.
  - SAINT hace exactamente lo que replica el agente: precios, impuestos, comprometido y cómo deja un pedido u OC cuando se cumple en partes.

**2. Facturación parcial (D5): decisión tomada, como acordamos**
Cada factura de un pedido sincronizado crea en el PV una **OC nueva idéntica a la factura**. El trabajador la busca en Compras por el número de factura, igual que en F3. A la **OC original** se le resta lo facturado, como hace SAINT cuando se recibe en partes: queda mostrando lo que falta por llegar y, cuando ya no falta nada, se borra. El comprometido del PV queda cuadrado.

**3. Lo que el cliente debe resolver antes de producción**
1. **Hay integraciones anteriores funcionando que hacen lo mismo que SigmaSync.** Si siguen prendidas, van a duplicar pedidos.
   - **Fábrica/CEDI:** los servicios `CEDIProcessorWorker1` y `CEDIProcessorWorker2` (POSTransferSystem, en `C:\cediworker1` y `C:\cediworker2`). Leen una cola en Supabase cada 5 minutos.
   - **PV Mayorista:**
     - el servicio `SyncPedidos` (`C:\posworker`). Lee las OC de `ADM_MAY` cada 5 minutos y las sube a Supabase. No transfiere nada desde el 29/04, pero sigue corriendo;
     - la tarea programada `sync compras` (`C:\SyncStoreSaint`, de 2024). Parece inactiva.

   Hay que apagarlas o desinstalarlas. Además, sus archivos de configuración tienen **usuarios, claves y llaves en texto plano**, así que conviene cambiar esas claves.
2. **El consecutivo de pedidos de Fábrica está bloqueando al propio SAINT.** Al grabar un pedido en pantalla sale el error 1004 ("número ya registrado"), porque `PrxPedido` quedó por debajo de los pedidos 035841–035849 que dejó la integración anterior. Hoy nadie puede grabar pedidos en Fábrica. El arreglo es poner `PrxPedido` en 35850, o en el siguiente libre.
3. **La base del CEDI está atrasada respecto a su versión de SAINT.** Le faltan unas 30 columnas que Fábrica sí tiene. Al grabar un pedido sale "columna 'Status' no válida", y al facturarlo, "'Estado' no válida". Los datos se guardan bien, pero el usuario ve un error cada vez. Como el CEDI nunca había usado pedidos, nadie lo había notado. Hay que pedir al soporte de SAINT que actualice la base `BODEGA1`.
4. **Dos hábitos de trabajo, sin los cuales la integración no sirve:**
   - **Fábrica y el CEDI deben facturar al PV cargando el pedido**, con Facturación → Cargar → Pedido. Hoy Fábrica le factura directo. Si sigue así, los pedidos se acumulan abiertos y la OC del PV nunca se cierra.
   - **El PV debe registrar sus compras cargando la OC**, con Compras → Cargar → Orden de compra, buscándola por el número de factura. Hoy ninguna compra del PV se ha hecho así: todas se digitan a mano. Hay que capacitarlos para cargar siempre la OC nueva, la de la factura, y no la original.

   Durante el primer mes del piloto revisaré cada semana que estos hábitos se cumplan, con unas consultas que dejé en el manual.

**4. Lo que sigue pendiente del cliente (sin cambios)**
1. Usuario `SYNC` creado desde SAINT en cada base.
2. Lista real de PV y el código de cliente de cada uno en Fábrica y en el CEDI. El PV Mayorista ya está completo y tiene creados los dos proveedores.
3. Servidor del hub (propongo el de Fábrica): encendido 24/7, salida por el puerto 443 y la cuenta o dominio de Cloudflare.
4. Datos del correo (SMTP) para las alertas.
5. Confirmar que les sirve una latencia de menos de 2 minutos.

**5. Siguiente paso**
Con los puntos 3 y 4 resueltos, instalo el hub y los agentes y hacemos la prueba de aceptación con el cliente sobre las copias que ya están montadas en sus servidores (`*_SYNCPRB`). Es lo único que no hemos visto en pantalla: que SAINT cargue los documentos que crea el agente. Después arrancamos el piloto con el PV Mayorista. Las copias de prueba se borran al terminar.

Quedo atento,
José
