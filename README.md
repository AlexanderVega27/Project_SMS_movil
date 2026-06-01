# Sistema de Monitoreo y Notificaciones Transaccionales (SMS / WhatsApp)

## 📋 Descripción del Proyecto
Este proyecto consiste en el codiseño e implementación de un sistema optimizado de notificaciones transaccionales mediante mensajería masiva (SMS Gateway). Está enfocado en mejorar la trazabilidad, asegurar la entrega programada de alertas del servicio logístico y ofrecer una arquitectura robusta para el monitoreo de tareas en tiempo real.

> **Nota de Seguridad:** Las credenciales de base de datos, tokens de acceso a APIs de producción y cadenas de conexión originales han sido completamente removidas y reemplazadas por marcadores de posición genéricos (*placeholders*) para proteger la infraestructura institucional.

---

## 🚀 Tecnologías y Herramientas Utilizadas
* **Lenguaje:** C# (.NET)
* **Tipo de Servicio:** Worker Service / Servicio de segundo plano independiente
* **Base de Datos:** Integración lógica con entornos Oracle SQL
* **APIs de Comunicación:** Conectividad con proveedores de pasarelas SMS

---

## ⚙️ Características Principales
* **Despliegue Programado:** Lógica avanzada para la distribución de alertas basadas en ventanas horarias específicas.
* **Monitoreo Continuo:** Capacidad de supervisión del estado del servicio para garantizar alta disponibilidad.
* **Seguridad de Datos:** Diseño preparado para la gestión de variables mediante entornos de configuración seguros (`appsettings.json`).
