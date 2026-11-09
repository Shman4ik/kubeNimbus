# Microsoft Store listing text: French

The French Store listing (Partner Center → kubeNimbus → the submission → Store listings → French), translated from [`store-listing.md`](store-listing.md) at release 1.1.0. Every fenced block below is pasted as it stands. "What's new in this version" is left empty in this language, and the product name is the same as in English. When the English description, short description, features or search terms change, change this file in the same PR.

## Short description

```text
Un client Kubernetes (K8s) rapide, gratuit et open source pour Windows. Repérez les applications en difficulté, suivez les logs des Pods, ouvrez des terminaux de conteneur et modifiez le YAML. Natif, sans télémétrie, sans compte et sans abonnement.
```

## Description

```text
kubeNimbus est un client de bureau Kubernetes rapide et gratuit pour Windows. Repérez l'application en difficulté, voyez pourquoi elle échoue et ouvrez les logs dont vous avez besoin, le tout dans une seule application native. Sans télémétrie, sans compte, sans abonnement.

Trouvez le problème
Applications place en premier ce qui demande votre attention. Chaque application Argo CD, et chaque workload qu'aucune application ne suit, reçoit un verdict de santé et une raison en une ligne tirée du statut du cluster lui-même. Ouvrez une application pour voir les faits derrière ce verdict, ses Pods, une chronologie et la dernière exécution du Pod qui plante. L'explorateur Resources complet est à un clic.

Connectez-vous avec ce que vous utilisez déjà
kubeNimbus lit tous les contextes de $KUBECONFIG et de ~/.kube/config, y compris l'authentification par plugin exec pour AWS EKS, Google GKE et Azure AKS, ainsi que les clusters derrière un proxy. Ouvrez plusieurs clusters dans des onglets, sélectionnez plusieurs namespaces ou utilisez une liste de flotte couvrant les clusters connectés. Production, staging et développement ont des couleurs distinctes. Les mots de passe, jetons et certificats ne sont jamais copiés dans le stockage de l'application.

Inspectez sans quitter l'application
• Listes de ressources en direct pour chaque kind servi par votre cluster, ressources personnalisées comprises. Les listes se mettent à jour par les watches Kubernetes, avec recherche, tri et choix des colonnes.
• Logs de Pod avec suivi en direct, recherche, expressions régulières, filtres de niveau, saut vers les erreurs, logs du conteneur précédent pour un CrashLoopBackOff, et tous les Pods d'un workload fusionnés en un seul flux colorié par source.
• Détail d'un Pod : variables d'environnement avec valeurs des ConfigMap résolues et valeurs des Secret masquées jusqu'à ce que vous les demandiez ; conditions, sondes et QoS ; CPU et mémoire dans le temps.
• Un vrai terminal pour exec : vi, top et mc s'affichent correctement, et Ctrl+C atteint le conteneur. Ouvrez exec ou un shell de nœud dans votre propre terminal si vous préférez.
• Port-forward d'un Pod ou d'un Service, ouverture de l'adresse locale dans un navigateur, et redirections maintenues après la fermeture de leur onglet du dock.
• Volets Service, Ingress et NetworkPolicy qui expliquent si le trafic peut atteindre les Pods.
• Détail d'un nœud avec marge allouable par rapport au demandé, événements et utilisation mesurée. Une bande de capacité montre le CPU et la mémoire utilisés et demandés sur l'ensemble des nœuds.

Relisez les modifications avant de les appliquer
• Édition YAML avec server-side apply, validation stricte des champs et aperçu dry-run qui demande au serveur d'API ce qu'une modification ferait avant de la faire.
• Collez un manifeste pour créer une nouvelle ressource. Sélectionnez plusieurs ressources pour une suppression groupée avec une seule confirmation.
• Scale, rollout restart, suppression, cordon et drain. Un redémarrage, un drain et une suppression sont armés et nommés avant de partir, et une suppression sur un cluster de production demande toujours confirmation ; ailleurs, vous pouvez désactiver la confirmation de suppression. Scale demande d'abord le nouveau nombre de réplicas.

Les outils Kubernetes, réunis
• Parcourez les releases Helm, leurs values, manifestes et historique de révisions directement depuis le cluster, sans binaire Helm.
• Parcourez les Applications, ApplicationSets et AppProjects Argo CD ; lancez une synchronisation, un refresh ou arrêtez une synchronisation en cours via la connexion Kubernetes, sans seconde authentification.
• Revue d'accès RBAC : que puis-je faire ici, d'où vient l'accès d'un ServiceAccount, et qui peut faire X ?
• Ctrl+K trouve une commande ; Ctrl+P change de cluster ; F1 affiche tous les raccourcis. Les thèmes clair et sombre sont inclus.

Essayez avant de vous connecter
Choisissez « Explore demo cluster » sur le premier écran. L'application inclut des exemples d'applications, de Pods, de logs et de détails de ressources, sans cluster, sans identifiants et sans réseau. Les données d'exemple sont signalées partout ; exec, port-forward et l'application de YAML nécessitent un vrai cluster. L'interface de l'application est en anglais.

Vos clusters restent les vôtres
kubeNimbus est une application NativeAOT autonome, sans runtime .NET à installer. Elle ne se connecte qu'aux clusters que vous ouvrez, sans analytique, sans rapport de plantage ni vérification de mise à jour de sa part. Le Microsoft Store gère les mises à jour. Les identifiants restent dans votre chaîne kubeconfig ; la politique de confidentialité liste les fichiers locaux de paramètres et de session.

kubeNimbus est gratuit et open source sous licence MIT : https://github.com/Shman4ik/kubeNimbus

Du même développeur : pgNimbus, un client PostgreSQL conçu de la même façon, gratuit et disponible dans le Store : https://apps.microsoft.com/detail/9N6SZT42XJ24
```

## Product features

One line per field.

```text
S'ouvre sur Applications : santé et raison en une ligne pour chaque app
Application de bureau native : aucun runtime .NET à installer
Sans télémétrie ni compte ; les identifiants ne sont jamais stockés
Tous les kinds servis par votre cluster, ressources personnalisées comprises
Logs de Pod en direct : recherche, regex, filtres de niveau, saut d'erreur
Tous les Pods d'un workload dans un seul flux de logs fusionné
Un vrai terminal dans le conteneur (vi, top et mc fonctionnent)
Édition YAML avec server-side apply et aperçu dry-run
Scale, restart, suppression et drain ; les suppressions en production demandent toujours
Volets Service, Ingress et NetworkPolicy en langage clair
Synchronisation Argo CD et releases Helm, lues depuis le cluster
Revue d'accès RBAC : que puis-je faire, qui peut faire X
Plusieurs clusters à la fois, couleurs prod/staging/dev, listes de flotte
Cluster de démonstration intégré : essayez sans aucun cluster
Gratuit et open source (MIT)
```

## Search terms

```text
kubernetes
k8s
kubectl
client kubernetes
kubernetes gui
logs pod
helm
```

## Screenshot captions

In the order of the English listing.

```text
Applications : chaque app avec un verdict de santé et une raison en une ligne
Un seul Entrée pour le pourquoi : les faits derrière un verdict et la dernière exécution du Pod
Détail d'un Pod ancré en bas, avec les logs en direct
Édition YAML avec server-side apply
Une seule liste pour plusieurs clusters
Le sélecteur de cluster, avec les couleurs prod, staging et dev
Un vrai terminal dans le conteneur
RBAC : qui peut faire quoi
```
