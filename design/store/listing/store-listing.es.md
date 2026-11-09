# Microsoft Store listing text: Spanish

The Spanish Store listing (Partner Center → kubeNimbus → the submission → Store listings → Spanish), translated from [`store-listing.md`](store-listing.md) at release 1.1.0. Every fenced block below is pasted as it stands. "What's new in this version" is left empty in this language, and the product name is the same as in English. When the English description, short description, features or search terms change, change this file in the same PR.

## Short description

```text
Un cliente gráfico de Kubernetes (K8s) rápido, gratuito y de código abierto para Windows. Encuentra aplicaciones con problemas, sigue los logs de los Pods, abre terminales de contenedores y edita YAML. Nativo, sin telemetría, sin cuenta y sin suscripción.
```

## Description

```text
kubeNimbus es un cliente de escritorio de Kubernetes rápido y gratuito para Windows. Encuentra la aplicación que falla, ve por qué falla y abre los logs que necesitas, todo en una sola aplicación nativa. Sin telemetría, sin cuenta, sin suscripción.

Encuentra el problema
Applications pone primero lo que necesita atención. Cada aplicación de Argo CD, y cada carga de trabajo que ninguna aplicación gestiona, tiene un veredicto de salud y un motivo en una línea, tomado del propio estado del clúster. Abre una aplicación para ver los hechos que hay detrás de ese veredicto, sus Pods, una línea de tiempo y la última ejecución del Pod que falla. El explorador completo de Resources queda a un clic.

Conéctate con lo que ya usas
kubeNimbus lee todos los contextos de $KUBECONFIG y ~/.kube/config, incluido el inicio de sesión con plugins exec para AWS EKS, Google GKE y Azure AKS, y los clústeres detrás de un proxy. Abre varios clústeres en pestañas, selecciona varios namespaces o usa una lista de flota que abarca los clústeres conectados. Producción, staging y desarrollo tienen colores distintos. Las contraseñas, los tokens y los certificados nunca se copian al almacenamiento de la aplicación.

Inspecciona sin salir de la aplicación
• Listas de recursos en vivo para cada tipo que sirve tu clúster, incluidos los recursos personalizados. Las listas se actualizan mediante watches de Kubernetes, con búsqueda, ordenación y selección de columnas.
• Logs de Pods con seguimiento, búsqueda, expresiones regulares, filtros por nivel, saltos entre errores, logs del contenedor anterior para un CrashLoopBackOff y todos los Pods de una carga de trabajo combinados en un único flujo con un color por Pod.
• Detalle del Pod: variables de entorno con los valores de ConfigMap resueltos y los de Secret ocultos hasta que los pidas; condiciones, probes y QoS; CPU y memoria a lo largo del tiempo.
• Un terminal real para exec: vi, top y mc se dibujan bien, y Ctrl+C llega al contenedor. Abre exec o una shell de nodo en tu propio terminal si lo prefieres.
• Haz port-forward a un Pod o a un Service, abre la dirección local en el navegador y mantén los reenvíos activos aunque cierres su pestaña del panel.
• Paneles de Service, Ingress y NetworkPolicy que explican si el tráfico puede llegar a los Pods.
• Detalle de nodo con el margen entre lo asignable y lo solicitado, eventos y uso medido. Una franja de capacidad muestra la CPU y la memoria usadas y solicitadas en todos los nodos.

Revisa los cambios antes de aplicarlos
• Edición de YAML con server-side apply, validación estricta de campos y una vista previa con dry-run que pregunta al servidor de la API qué haría un cambio antes de hacerlo.
• Pega un manifiesto para crear un recurso nuevo. Selecciona varios recursos para eliminarlos de una vez con una sola confirmación.
• Escalar, rollout restart, eliminar, cordon y drain. Un reinicio, un drain y una eliminación se arman y se nombran antes de ejecutarse, y eliminar en un clúster de producción siempre pide confirmación; en los demás puedes desactivar la confirmación de eliminar. Escalar pide primero el nuevo número de réplicas.

Herramientas de Kubernetes, juntas
• Explora releases de Helm, sus values, manifiestos e historial de revisiones directamente desde el clúster, sin necesidad del binario de Helm.
• Explora Applications, ApplicationSets y AppProjects de Argo CD; haz sync, refresh o termina una sincronización en curso a través de la conexión de Kubernetes, sin un segundo inicio de sesión.
• Revisión de acceso RBAC: qué puedo hacer aquí, de dónde viene el acceso de un ServiceAccount y quién puede hacer X.
• Ctrl+K busca un comando; Ctrl+P cambia de clúster; F1 muestra todos los atajos. Incluye temas claro y oscuro.

Pruébalo antes de conectar
Elige "Explore demo cluster" en la primera pantalla. La aplicación incluye aplicaciones, Pods, logs y detalles de recursos de ejemplo, sin necesidad de clúster, credenciales ni red. Los datos de ejemplo están señalados en todas partes; exec, port-forward y la aplicación de YAML necesitan un clúster real. La interfaz de la aplicación está en inglés.

Tus clústeres siguen siendo tuyos
kubeNimbus es una aplicación NativeAOT autocontenida, sin ningún runtime de .NET que instalar. Se conecta solo a los clústeres que abres, sin analítica, informes de fallos ni comprobaciones de actualizaciones propias. Microsoft Store se encarga de las actualizaciones. Las credenciales se quedan en tu cadena de kubeconfig; la política de privacidad enumera los archivos locales de ajustes y de sesión.

kubeNimbus es gratuito y de código abierto con licencia MIT: https://github.com/Shman4ik/kubeNimbus

Del mismo desarrollador: pgNimbus, un cliente de PostgreSQL hecho de la misma forma, gratuito y disponible en la Store: https://apps.microsoft.com/detail/9N6SZT42XJ24
```

## Product features

One line per field.

```text
Se abre en Applications: salud y un motivo en una línea para cada app
Aplicación de escritorio nativa: sin runtime de .NET que instalar
Sin telemetría ni cuenta; las credenciales nunca se guardan
Todos los tipos que sirve tu clúster, recursos personalizados incluidos
Logs de Pods en vivo: búsqueda, regex, filtros por nivel, salto a errores
Todos los Pods de una carga de trabajo en un único log combinado
Un terminal real en el contenedor (vi, top y mc funcionan)
Edición de YAML con server-side apply y vista previa con dry-run
Escalar, reiniciar, eliminar y drain; eliminar en producción siempre pide confirmación
Paneles de Service, Ingress y NetworkPolicy en lenguaje claro
Sync de Argo CD y releases de Helm, leídos desde el clúster
Revisión de acceso RBAC: qué puedo hacer, quién puede hacer X
Varios clústeres a la vez, colores prod/staging/dev, listas de flota
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
Applications: cada app con un veredicto de salud y un motivo en una línea
Un Enter hasta el porqué: los hechos detrás de un veredicto y la última ejecución del Pod
Detalle del Pod acoplado en la parte inferior, con logs en vivo
Edición de YAML con server-side apply
Una lista que abarca varios clústeres
El selector de clústeres, con colores prod, staging y dev
Un terminal real en el contenedor
RBAC: quién puede hacer qué
```
