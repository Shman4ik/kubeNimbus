# Microsoft Store listing text: Italian

The Italian Store listing (Partner Center → kubeNimbus → the submission → Store listings → Italian), translated from [`store-listing.md`](store-listing.md) at release 1.1.0. Every fenced block below is pasted as it stands. "What's new in this version" is left empty in this language, and the product name is the same as in English. When the English description, short description, features or search terms change, change this file in the same PR.

## Short description

```text
Un client Kubernetes (K8s) con interfaccia grafica, veloce, gratuito e open source per Windows. Trova le applicazioni in errore, segui i log dei Pod, apri terminali nei container e modifica i file YAML. App nativa, senza telemetria, senza account e senza abbonamento.
```

## Description

```text
kubeNimbus è un client Kubernetes desktop veloce e gratuito per Windows. Trova l'applicazione in errore, scopri perché fallisce e apri i log che ti servono, tutto in un'unica app nativa. Senza telemetria, senza account, senza abbonamento.

Trova il problema
La vista Applications mostra per prime le cose che richiedono attenzione. Ogni applicazione Argo CD, e ogni workload di cui nessuna applicazione tiene traccia, ha un verdetto di salute e una spiegazione di una riga ricavata dallo stato del cluster stesso. Apri un'applicazione per vedere su cosa si basa quel verdetto: i suoi Pod, una timeline e l'ultima esecuzione del Pod che va in crash. L'explorer completo di Resources è a un clic di distanza.

Connettiti con ciò che usi già
kubeNimbus legge ogni contesto in $KUBECONFIG e in ~/.kube/config, compreso l'accesso tramite exec-plugin per AWS EKS, Google GKE e Azure AKS, e i cluster dietro un proxy. Apri più cluster in schede, seleziona più namespace oppure usa una lista unica per tutti i cluster connessi. Produzione, staging e sviluppo hanno colori distinti. Password, token e certificati non vengono mai copiati nello spazio di archiviazione dell'app.

Ispeziona senza uscire dall'app
• Liste di risorse in tempo reale per ogni tipo di risorsa che il tuo cluster espone, risorse personalizzate comprese. Le liste si aggiornano tramite i watch di Kubernetes, con ricerca, ordinamento e scelta delle colonne.
• Log dei Pod con follow, ricerca, espressioni regolari, filtri per livello, salto agli errori, log del container precedente per un CrashLoopBackOff, e tutti i Pod di un workload uniti in un unico flusso con un colore per ciascuno.
• Dettaglio del Pod: variabili d'ambiente con i valori dei ConfigMap risolti e i valori dei Secret nascosti finché non li chiedi; condizioni, probe e QoS; CPU e memoria nel tempo.
• Un vero terminale per exec: vi, top e mc vengono visualizzati correttamente e Ctrl+C arriva al container. Se preferisci, apri exec o una shell sul nodo nel tuo terminale.
• Esegui il port-forward di un Pod o di un Service, apri l'indirizzo locale in un browser e lascia i forward attivi anche dopo la chiusura della loro scheda nel dock.
• Pannelli per Service, Ingress e NetworkPolicy che spiegano se il traffico può raggiungere i Pod.
• Dettaglio del nodo con margine tra allocabile e richiesto, eventi e utilizzo misurato. Una barra della capacità mostra CPU e memoria usate e richieste su tutti i nodi.

Rivedi le modifiche prima di applicarle
• Modifica YAML con server-side apply, validazione rigorosa dei campi e un'anteprima dry-run che chiede al server API cosa farebbe una modifica prima di applicarla.
• Incolla un manifest per creare una nuova risorsa. Seleziona più risorse per eliminarle in blocco con una sola conferma.
• Scale, rollout restart, eliminazione, cordon e drain. Un riavvio, un drain e un'eliminazione partono solo dopo una conferma che indica il nome della risorsa, e un'eliminazione su un cluster di produzione chiede sempre conferma; altrove puoi disattivare la conferma di eliminazione. Scale chiede prima il nuovo numero di repliche.

Gli strumenti Kubernetes, insieme
• Sfoglia release Helm, valori, manifest e cronologia delle revisioni direttamente dal cluster, senza bisogno del binario Helm.
• Sfoglia Application, ApplicationSet e AppProject di Argo CD; esegui sync o refresh, oppure termina una sync in corso, tramite la connessione Kubernetes, senza un secondo accesso.
• Revisione degli accessi RBAC: cosa posso fare qui, da dove arriva l'accesso di un ServiceAccount e chi può fare X?
• Ctrl+K trova un comando; Ctrl+P cambia cluster; F1 mostra tutte le scorciatoie. Sono inclusi i temi chiaro e scuro.

Provalo prima di connetterti
Scegli "Explore demo cluster" nella prima schermata. L'app include applicazioni, Pod, log e dettagli di risorse di esempio, senza bisogno di un cluster, di credenziali o della rete. I dati di esempio sono indicati come tali ovunque; exec, port-forward e l'applicazione dello YAML richiedono un cluster reale. L'interfaccia dell'app è in inglese.

I tuoi cluster restano tuoi
kubeNimbus è un'app NativeAOT autonoma, senza runtime .NET da installare. Si connette solo ai cluster che apri, senza analytics, segnalazioni di arresti anomali o controlli degli aggiornamenti propri. Gli aggiornamenti dell'app li gestisce il Microsoft Store. Le credenziali restano nella tua catena di kubeconfig; l'informativa sulla privacy elenca i file locali di impostazioni e di sessione.

kubeNimbus è gratuito e open source con licenza MIT: https://github.com/Shman4ik/kubeNimbus

Dello stesso sviluppatore: pgNimbus, un client PostgreSQL costruito allo stesso modo, gratuito e disponibile nello Store: https://apps.microsoft.com/detail/9N6SZT42XJ24
```

## Product features

One line per field.

```text
Si apre su Applications: stato di salute e spiegazione in una riga per ogni app
App desktop nativa: nessun runtime .NET da installare
Senza telemetria né account; le credenziali non vengono mai salvate
Ogni tipo di risorsa del cluster, risorse personalizzate comprese
Log dei Pod in tempo reale: ricerca, regex, filtri per livello, salto agli errori
Tutti i Pod di un workload in un unico flusso di log unito
Un vero terminale nel container (vi, top e mc funzionano)
Modifica YAML con server-side apply e anteprima dry-run
Scale, restart, eliminazione e drain; in produzione l'eliminazione chiede sempre conferma
Pannelli Service, Ingress e NetworkPolicy in parole semplici
Sync di Argo CD e release Helm, lette dal cluster
Revisione degli accessi RBAC: cosa posso fare, chi può fare X
Più cluster insieme, colori prod/staging/dev, liste multi-cluster
Cluster demo integrato: provalo senza alcun cluster
Gratuito e open source (MIT)
```

## Search terms

```text
kubernetes
k8s
kubectl
client kubernetes
interfaccia kubernetes
log dei pod
helm
```

## Screenshot captions

In the order of the English listing.

```text
Applications: ogni app con un verdetto di salute e una spiegazione in una riga
Con Invio arrivi al perché: i fatti dietro un verdetto e l'ultima esecuzione del Pod
Dettaglio del Pod agganciato in basso, con i log in tempo reale
Modifica YAML con server-side apply
Un'unica lista su più cluster
Il selettore di cluster, con i colori prod, staging e dev
Un vero terminale nel container
RBAC: chi può fare cosa
```
