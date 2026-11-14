# Microsoft Store listing text: German

The German Store listing (Partner Center → kubeNimbus → the submission → Store listings → German), translated from [`store-listing.md`](store-listing.md) at release 1.1.0, with the additions of 2026-11-14. Every fenced block below is pasted as it stands. "What's new in this version" is left empty in this language, and the product name is the same as in English. When the English description, short description, features or search terms change, change this file in the same PR.

## Short description

```text
Ein schneller, kostenloser Open-Source-Kubernetes-Client (K8s) mit grafischer Oberfläche für Windows. Finden Sie fehlerhafte Anwendungen, verfolgen Sie Pod-Logs, sehen Sie CPU und Arbeitsspeicher ohne Prometheus-Installation, öffnen Sie Container-Terminals und bearbeiten Sie YAML. Meldet sich über Ihre kubeconfig bei AWS EKS, Google GKE und Azure AKS an. Nativ, ohne Telemetrie, ohne Konto und ohne Abonnement.
```

## Description

```text
kubeNimbus ist ein schneller, kostenloser Kubernetes-Desktop-Client für Windows. Finden Sie die fehlerhafte Anwendung, sehen Sie, warum sie fehlschlägt, und öffnen Sie die Logs, die Sie brauchen, alles in einer nativen App. Keine Telemetrie, kein Konto, kein Abonnement.

Das Problem finden

Die Ansicht Applications zeigt zuerst, was Aufmerksamkeit braucht. Jede Argo CD Application und jeder Workload, den keine Application verwaltet, erhält einen Health-Status und eine einzeilige Begründung aus dem eigenen Status des Clusters. Öffnen Sie eine App, um die Fakten hinter dieser Bewertung zu sehen: ihre Pods, eine Zeitleiste und den letzten Lauf des abstürzenden Pods. Der vollständige Resources-Explorer ist einen Klick entfernt.

Mit dem verbinden, was Sie schon nutzen

kubeNimbus liest jeden Kontext in $KUBECONFIG und ~/.kube/config, einschließlich der Anmeldung per exec-Plugin für AWS EKS, Google GKE und Azure AKS, und erreicht auch Cluster hinter einem Proxy. Eine abgelaufene Sitzung wird wieder aufgenommen, ohne dass der Tab geschlossen werden muss. Bei eingeschränkten Berechtigungen zeigt kubeNimbus, was Sie lesen dürfen, und sagt, was es nicht lesen konnte. Öffnen Sie mehrere Cluster in Tabs, wählen Sie mehrere Namespaces aus oder nutzen Sie eine gemeinsame Liste über alle verbundenen Cluster. Production, Staging und Development haben jeweils eigene Farben. Passwörter, Tokens und Zertifikate werden nie in den Speicher der App kopiert.

CPU und Arbeitsspeicher, ohne etwas zu installieren

kubeNimbus liest die Metrics-API, die Ihr Cluster bereits bereitstellt (metrics-server). Pod- und Node-Listen zeigen CPU und Arbeitsspeicher live, die Pod-Details stellen beides für die letzten 30 Minuten Ihrer Sitzung als Diagramm dar, und die Nodes-Liste zeigt genutzte und angeforderte Kapazität. Weder Prometheus noch ein Agent im Cluster ist nötig. Auf einem Cluster ohne metrics-server werden diese Spalten ausgeblendet, statt Fehler anzuzeigen.

Untersuchen, ohne die App zu verlassen

• Live-Ressourcenlisten für jeden Ressourcentyp, den Ihr Cluster bereitstellt, einschließlich benutzerdefinierter Ressourcen. Die Listen aktualisieren sich über Kubernetes-Watches, mit Suche, Sortierung und Spaltenauswahl.
• Pod-Logs mit Follow, Suche, regulären Ausdrücken, Level-Filtern, Sprüngen zu Fehlern, Logs des vorherigen Containers bei CrashLoopBackOff sowie allen Pods eines Workloads, zusammengeführt in einem farbcodierten Stream.
• Pod-Details: Umgebungsvariablen mit aufgelösten ConfigMap-Werten und maskierten Secret-Werten, bis Sie sie anfordern; Conditions, Probes und QoS; CPU und Arbeitsspeicher im Zeitverlauf.
• Ein echtes Terminal für exec: vi, top und mc werden korrekt dargestellt, und Strg+C erreicht den Container. Öffnen Sie exec oder eine Node-Shell auf Wunsch in Ihrem eigenen Terminal.
• Port-Forwarding für einen Pod oder Service: die lokale Adresse im Browser öffnen und Weiterleitungen weiterlaufen lassen, auch nachdem ihr Tab im Dock geschlossen wurde.
• Ansichten für Service, Ingress und NetworkPolicy, die erklären, ob Datenverkehr die Pods erreichen kann.
• Node-Details mit Spielraum zwischen zuweisbaren und angeforderten Ressourcen, Events und gemessener Auslastung. Eine Kapazitätsleiste zeigt für CPU und Arbeitsspeicher, wie viel über alle Nodes genutzt und angefordert ist.

Änderungen prüfen, bevor sie ausgeführt werden

• YAML-Bearbeitung mit server-side apply, strikter Feldvalidierung und einer dry-run-Vorschau, die den API-Server fragt, was eine Änderung bewirken würde, bevor sie ausgeführt wird.
• Fügen Sie ein Manifest ein, um eine neue Ressource zu erstellen. Wählen Sie mehrere Ressourcen aus und löschen Sie sie gemeinsam mit nur einer Bestätigung.
• Skalieren, rollout restart, Löschen, cordon und drain. Ein Neustart, ein drain und ein Löschen werden erst nach einer Bestätigung ausgeführt, die das Ziel nennt, und ein Löschen in einem Production-Cluster fragt immer nach; anderswo können Sie die Löschbestätigung abschalten. Beim Skalieren wird zuerst die neue Anzahl abgefragt.

Kubernetes-Werkzeuge an einem Ort

• Helm-Releases, Values, Manifeste und den Revisionsverlauf direkt aus dem Cluster ansehen, ohne dass eine Helm-Binärdatei nötig ist.
• Argo CD Applications, ApplicationSets und AppProjects ansehen; Sync und Refresh auslösen oder einen laufenden Sync beenden, über die Kubernetes-Verbindung und ohne zweite Anmeldung.
• RBAC-Zugriffsprüfung: Was darf ich hier, woher kommt der Zugriff eines ServiceAccounts, und wer darf X?
• Mit Strg+K finden Sie einen Befehl, mit Strg+P wechseln Sie den Cluster, und F1 zeigt alle Tastenkürzel. Helle und dunkle Designs sind enthalten.

Vor dem Verbinden ausprobieren

Wählen Sie auf dem ersten Bildschirm "Explore demo cluster". Die App enthält Beispielanwendungen, Pods, Logs und Ressourcendetails, ohne dass ein Cluster, Zugangsdaten oder ein Netzwerk nötig sind. Beispieldaten sind überall gekennzeichnet; exec, port-forward und das Anwenden von YAML benötigen einen echten Cluster. Die Oberfläche der App ist auf Englisch.

Ihre Cluster gehören Ihnen

kubeNimbus ist eine eigenständige NativeAOT-App, ohne dass eine .NET-Runtime installiert werden muss. Sie verbindet sich nur mit den Clustern, die Sie öffnen, ohne eigene Analysen, Absturzberichte oder Update-Prüfungen. Die App-Updates übernimmt der Microsoft Store. Zugangsdaten bleiben in Ihrer kubeconfig-Kette; die Datenschutzerklärung listet die lokalen Einstellungs- und Sitzungsdateien auf.

kubeNimbus ist kostenlos und Open Source unter der MIT-Lizenz: https://github.com/Shman4ik/kubeNimbus

Vom selben Entwickler: pgNimbus, ein PostgreSQL-Client, auf die gleiche Weise gebaut, kostenlos und im Store: https://apps.microsoft.com/detail/9N6SZT42XJ24
```

## Product features

One line per field.

```text
Startet mit Applications: Health-Status und einzeilige Begründung für jede App
Native Desktop-App: keine .NET-Runtime nötig
Keine Telemetrie, kein Konto; Zugangsdaten werden nie gespeichert
Jeder Ressourcentyp Ihres Clusters, auch benutzerdefinierte Ressourcen
CPU und Arbeitsspeicher aus metrics-server: kein Prometheus nötig
Anmeldung bei AWS EKS, Google GKE und Azure AKS aus Ihrer kubeconfig
Live-Pod-Logs: Suche, Regex, Level-Filter, Sprung zum Fehler
Alle Pods eines Workloads in einem zusammengeführten Log-Stream
Ein echtes Terminal im Container (vi, top und mc funktionieren)
YAML-Bearbeitung mit server-side apply und dry-run-Vorschau
Skalieren, Neustart, Löschen und drain; Löschen in Production fragt immer nach
Service-, Ingress- und NetworkPolicy-Ansichten in klaren Worten
Argo CD Sync und Helm-Releases, direkt aus dem Cluster gelesen
RBAC-Zugriffsprüfung: Was darf ich, wer darf X
Viele Cluster gleichzeitig, Farben für Prod/Staging/Dev, clusterübergreifende Listen
Integrierter Demo-Cluster: ohne eigenen Cluster ausprobieren
Kostenlos und Open Source (MIT)
```

## Search terms

```text
kubernetes
k8s
kubectl
kubernetes gui
kubernetes oberfläche
pod logs anzeigen
helm
```

## Screenshot captions

In the order of the English listing.

```text
Applications: jede App mit Health-Status und einzeiliger Begründung
Mit Enter zur Ursache: die Fakten hinter einer Bewertung und der letzte Lauf des Pods
Pod-Details unten angedockt, mit Live-Logs
YAML-Bearbeitung mit server-side apply
Eine Liste über mehrere Cluster
Der Cluster-Umschalter mit Farben für Prod, Staging und Dev
Ein echtes Terminal im Container
RBAC: wer was darf
```
