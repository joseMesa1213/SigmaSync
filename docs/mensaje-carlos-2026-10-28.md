**Asunto:** Sincronización SAINT – inicio de diseño: 1 decisión, 1 alerta y pendientes del cliente

Hola Carlos,

Arrancamos la fase de diseño técnico. Te comparto el borrador del documento (`docs/diseno-tecnico.md`). Te pido una decisión tuya y que gestionemos con el cliente unos puntos; ninguno frena el desarrollo de esta semana, pero sí el cierre de F2 y la salida a producción.

**1. Decisión: facturación parcial en F2 (necesito tu visto bueno)**
Como en Compras la OC se carga sin poder editar las líneas, la OC tiene que quedar idéntica a cada factura. Propuesta:
- Si la factura cubre todo el pedido, el agente ajusta la OC original.
- Si la cubre en parte, la OC original queda con lo facturado y lo pendiente pasa a una OC nueva del PV. Las siguientes facturas de ese pedido ajustan esa OC nueva.
- El trabajador del PV siempre encuentra la OC buscando por el número de factura.
¿Te parece bien o prefieres otra regla?

**2. Alerta para el cliente (bloquea la salida a producción de F1)**
En Fábrica, el consecutivo de pedidos (`PrxPedido` = 35841) quedó por debajo de pedidos que ya existen (035841 a 035849). Los creó la integración anterior "WORKER". El próximo pedido que se grabe en SAINT va a chocar. Hay que pedir al cliente o al soporte de SAINT que lo corrijan, y que confirmen que esa integración ya no está instalada.

**3. Hallazgo: las versiones de SAINT no son iguales**
La base de Fábrica tiene una versión de SAINT más nueva que la del PV y la del CEDI (tiene columnas de facturación electrónica que las otras no tienen). El alcance suponía la misma versión en todos los nodos. Ya lo contemplé en el diseño (el agente escribe solo los campos comunes y valida la estructura al arrancar), pero conviene que el cliente lo sepa, por si planea actualizar SAINT en algún nodo durante el proyecto.

**4. Lo que necesitamos del cliente**
1. **Ambiente de pruebas con SAINT instalado** (copias de PV Mayorista, Fábrica y CEDI), con alguien que pueda hacer tres pruebas en pantalla:
   - Grabar una compra cargando una OC, para ver qué hace SAINT con la OC.
   - Crear una OC, para ver cómo afecta las cantidades pedidas.
   - Crear un pedido en el CEDI, que nunca los ha usado, para tener un modelo de referencia.
2. **Condición operativa:** para que F2 funcione, Fábrica y CEDI tienen que facturar cargando el pedido sincronizado (lo buscan por número de OC). Hoy facturan directo; si no cambian ese hábito, todo entra como F3.
3. **Regla en los PV:** las OC a Fábrica se hacen con el proveedor `890919355-4` y las del CEDI con `8909193545`. Hay que confirmar que cada PV tiene ambos proveedores creados.
4. **Usuario "SYNC"** creado en SAINT en cada base, para que los documentos de la integración queden identificados.
5. **Número real de PV** y, para cada uno, su código de cliente en Fábrica y en el CEDI.
6. **Servidor del hub:** propongo el de Fábrica (también aloja el CEDI). Necesito confirmar que está encendido 24/7, que tiene salida a internet por el puerto 443 y quién crea la cuenta o el dominio de Cloudflare.
7. **Latencia:** propongo menos de 2 minutos entre que se graba un documento y aparece en el otro nodo. ¿Les sirve?

**Qué sigue de mi lado:** esta semana arranco la infraestructura (esquema de sincronización, hub y agente) y después F1, todo sobre las copias locales. Nada de eso depende de los puntos anteriores.

Quedo atento,
José
