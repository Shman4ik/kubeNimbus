# Microsoft Store listing text: Spanish

The Spanish Store listing (Partner Center → kubeNimbus → the submission → Store listings → Spanish), translated from [`store-listing.md`](store-listing.md) at release 1.1.0, with the additions of 2026-11-14. Every fenced block below is pasted as it stands. "What's new in this version" is left empty in this language, and the product name is the same as in English. When the English description, short description, features or search terms change, change this file in the same PR.

## Short description

```text
Un cliente gráfico de Kubernetes (K8s) rápido, gratuito y de código abierto para Windows. Encuentra aplicaciones con problemas, sigue los logs de los Pods, ve la CPU y la memoria sin instalar Prometheus, abre terminales en contenedores y edita YAML. Inicia sesión en AWS EKS, Google GKE y Azure AKS a través de tu kubeconfig. Nativo, sin telemetría, sin cuenta y sin suscripción.
```

## Description

```text
kubeNimbus es un cliente de escritorio de Kubernetes rápido y gratuito para Windows. Encuentra la aplicación con problemas, descubre por qué falla y abre los logs que necesitas, todo en una sola aplicación nativa. Sin telemetría, sin cuenta, sin suscripción.

Encuentra el problema
La vista Applications muestra primero lo que necesita atención. Cada aplicación de Argo CD, y cada carga de trabajo que ninguna aplicación gestiona, tiene un veredicto de salud y una explicación en una línea, ambos tomados del propio estado del clúster. Abre una aplicación para ver en qué se basa ese veredicto: sus Pods, una línea de tiempo y la última ejecución del Pod que falla. El explorador completo de Resources queda a un clic.

Conéctate con lo que ya usas
kubeNimbus lee todos los contextos de $KUBECONFIG y ~/.kube/config, incluido el inicio de sesión con plugins exec para AWS EKS, Google GKE y Azure AKS, y también clústeres detrás de un proxy. Una sesión caducada se recupera sin cerrar la pestaña. Con permisos limitados, kubeNimbus muestra lo que puedes leer e indica lo que no pudo leer. Abre varios clústeres en pestañas, selecciona varios namespaces o usa una lista única con todos los clústeres conectados. Producción, staging y desarrollo tienen colores distintos. Las contraseñas, los tokens y los certificados nunca se copian al almacenamiento de la aplicación.

CPU y memoria, sin nada que instalar
kubeNimbus lee la API de métricas que tu clúster ya ofrece (metrics-server). Las listas de Pods y nodos muestran la CPU y la memoria en vivo, el detalle del Pod las representa en gráficos de los últimos 30 minutos de tu sesión, y la lista Nodes muestra la capacidad usada y solicitada. No hacen falta Prometheus ni ningún agente en el clúster. En un clúster sin metrics-server, esas columnas se ocultan en lugar de mostrar errores.

Inspecciona sin salir de la aplicación
• Listas de recursos en vivo para cada tipo de recurso que ofrece tu clúster, incluidos los recursos personalizados. Las listas se actualizan mediante watches de Kubernetes, con búsqueda, ordenación y selección de columnas.
• Logs de Pods con seguimiento, búsqueda, expresiones regulares, filtros por nivel, saltos entre errores, logs del contenedor anterior para un CrashLoopBackOff y todos los Pods de una carga de trabajo combinados en un único flujo con un color por Pod.
• Detalle del Pod: variables de entorno con los valores de ConfigMap resueltos y los de Secret ocultos hasta que los pidas; condiciones, probes y QoS; CPU y memoria a lo largo del tiempo.
• Un terminal real para exec: vi, top y mc se muestran correctamente, y Ctrl+C llega al contenedor. Abre exec o una shell de nodo en tu propio terminal si lo prefieres.
• Haz port-forward a un Pod o a un Service, abre la dirección local en el navegador y mantén los reenvíos activos aunque cierres su pestaña en el panel inferior.
• Paneles de Service, Ingress y NetworkPolicy que explican si el tráfico puede llegar a los Pods.
• Detalle de nodo con el margen entre lo asignable y lo solicitado, eventos y uso medido. Una franja de capacidad muestra la CPU y la memoria usadas y solicitadas en todos los nodos.

Revisa los cambios antes de aplicarlos
• Edición de YAML con server-side apply, validación estricta de campos y una vista previa con dry-run que pregunta al servidor de la API qué haría un cambio antes de aplicarlo.
• Pega un manifiesto para crear un recurso nuevo. Selecciona varios recursos para eliminarlos de una vez con una sola confirmación.
• Escalar, rollout restart, eliminar, cordon y drain. Un reinicio, un drain y una eliminación solo se ejecutan tras una confirmación que nombra el objetivo, y eliminar en un clúster de producción siempre pide confirmación; en los demás puedes desactivar la confirmación de eliminar. Escalar pide primero el nuevo número de réplicas.

Herramientas de Kubernetes, juntas
• Explora releases de Helm, sus values, manifiestos e historial de revisiones directamente desde el clúster, sin necesidad del binario de Helm.
• Explora Applications, ApplicationSets y AppProjects de Argo CD; haz sync o refresh, o termina una sincronización en curso, a través de la conexión de Kubernetes, sin un segundo inicio de sesión.
• Revisión de acceso RBAC: qué puedo hacer aquí, de dónde viene el acceso de un ServiceAccount y quién puede hacer X.
• Ctrl+K busca un comando; Ctrl+P cambia de clúster; F1 muestra todos los atajos. Incluye temas claro y oscuro.

Pruébalo antes de conectar
Elige "Explore demo cluster" en la primera pantalla. La aplicación incluye aplicaciones, Pods, logs y detalles de recursos de ejemplo, sin necesidad de clúster, credenciales ni red. Los datos de ejemplo están señalados en todas partes; exec, port-forward y la aplicación de YAML necesitan un clúster real. La interfaz de la aplicación está en inglés.

Tus clústeres siguen siendo tuyos
kubeNimbus es una aplicación NativeAOT autocontenida, sin ningún runtime de .NET que instalar. Se conecta solo a los clústeres que abres, sin analítica, informes de fallos ni comprobaciones de actualizaciones propias. Microsoft Store se encarga de las actualizaciones. Las credenciales se quedan en tu cadena de kubeconfig; la política de privacidad enumera los archivos locales de ajustes y de sesión.

kubeNimbus es gratuito y de código abierto con licencia MIT: https://github.com/Shman4ik/kubeNimbus

Del mismo desarrollador: pgNimbus, un cliente de PostgreSQL hecho de la misma forma, gratuito y disponible en Microsoft Store: https://apps.microsoft.com/detail/9N6SZT42XJ24
```

## Product features

One line per field.

```text
Se abre en Applications: estado de salud y una explicación en una línea para cada app
Aplicación de escritorio nativa: sin runtime de .NET que instalar
Sin telemetría ni cuenta; las credenciales nunca se guardan
Todos los tipos de recurso de tu clúster, recursos personalizados incluidos
CPU y memoria desde metrics-server: sin Prometheus
Inicio de sesión en AWS EKS, Google GKE y Azure AKS desde tu kubeconfig
Logs de Pods en vivo: búsqueda, regex, filtros por nivel, salto a errores
Todos los Pods de una carga de trabajo en un único log combinado
Un terminal real en el contenedor (vi, top y mc funcionan)
Edición de YAML con server-side apply y vista previa con dry-run
Escalar, reiniciar, eliminar y drain; eliminar en producción siempre pide confirmación
Paneles de Service, Ingress y NetworkPolicy en lenguaje claro
Sync de Argo CD y releases de Helm, leídos desde el clúster
Revisión de acceso RBAC: qué puedo hacer, quién puede hacer X
Varios clústeres a la vez, colores prod/staging/dev, listas multiclúster
Clúster de demostración integrado: pruébalo sin ningún clúster
Gratuito y de código abierto (MIT)
```

## Search terms

```text
kubernetes
k8s
kubectl
cliente kubernetes
kubernetes gui
logs de pods
helm
```

## Screenshot captions

In the order of the English listing.

```text
Applications: cada app con un veredicto de salud y una explicación en una línea
Enter para entender: los hechos detrás de un veredicto y la última ejecución del Pod
Detalle del Pod acoplado en la parte inferior, con logs en vivo
Edición de YAML con server-side apply
Una lista que abarca varios clústeres
El selector de clústeres, con colores prod, staging y dev
Un terminal real en el contenedor
RBAC: quién puede hacer qué
```
