# Microsoft Store listing text: Portuguese (Brazil)

The Brazilian Portuguese Store listing (Partner Center → kubeNimbus → the submission → Store listings → Portuguese (Brazil)), translated from [`store-listing.md`](store-listing.md) at release 1.1.0. Every fenced block below is pasted as it stands. "What's new in this version" is left empty in this language, and the product name is the same as in English. When the English description, short description, features or search terms change, change this file in the same PR.

## Short description

```text
Um cliente GUI de Kubernetes (K8s) rápido, gratuito e de código aberto para Windows. Encontre aplicações com problemas, acompanhe logs de Pods, abra terminais em contêineres e edite YAML. Nativo, sem telemetria, sem conta e sem assinatura.
```

## Description

```text
O kubeNimbus é um cliente desktop de Kubernetes rápido e gratuito para Windows. Encontre a aplicação com problemas, veja por que ela está falhando e abra os logs de que você precisa, tudo em um único app nativo. Sem telemetria, sem conta, sem assinatura.

Encontre o problema
A tela "Applications" mostra primeiro o que precisa de atenção. Cada aplicação do Argo CD, e cada workload que nenhuma aplicação acompanha, tem um veredito de saúde e uma explicação em uma linha, ambos tirados do próprio status do cluster. Abra uma aplicação para ver em que se baseia esse veredito: seus Pods, uma linha do tempo e a última execução do Pod que está falhando. O explorador completo de "Resources" está a um clique de distância.

Conecte-se com o que você já usa
O kubeNimbus lê todos os contextos em $KUBECONFIG e ~/.kube/config, inclusive o login por plugin exec do AWS EKS, do Google GKE e do Azure AKS, e também clusters atrás de proxy. Abra vários clusters em abas, selecione vários namespaces ou use uma lista única com todos os clusters conectados. Produção, homologação (staging) e desenvolvimento têm cores distintas. Senhas, tokens e certificados nunca são copiados para o armazenamento do app.

Inspecione sem sair do app
• Listas de recursos ao vivo para todos os tipos de recurso que o seu cluster oferece, inclusive recursos personalizados. As listas se atualizam por watches do Kubernetes, com busca, ordenação e seleção de colunas.
• Logs de Pods com follow, busca, expressões regulares, filtros por nível, saltos para erros, logs do contêiner anterior para um CrashLoopBackOff e todos os Pods de um workload reunidos em um único fluxo com cores por Pod.
• Detalhe do Pod: variáveis de ambiente com valores de ConfigMap resolvidos e valores de Secret mascarados até você pedir; conditions, probes e QoS; CPU e memória ao longo do tempo.
• Um terminal de verdade para exec: vi, top e mc são exibidos corretamente, e Ctrl+C chega ao contêiner. Abra o exec ou um shell de nó no seu próprio terminal, se preferir.
• Faça port-forward de um Pod ou Service, abra o endereço local no navegador e mantenha os forwards rodando depois que a aba do dock for fechada.
• Painéis de Service, Ingress e NetworkPolicy que explicam se o tráfego consegue chegar aos Pods.
• Detalhe do nó com folga entre o alocável e o requisitado, eventos e uso medido. Uma faixa de capacidade mostra a CPU e a memória usadas e requisitadas em todos os nós.

Revise as alterações antes de aplicá-las
• Edição de YAML com server-side apply, validação estrita de campos e uma prévia de dry-run que pergunta ao servidor de API o que uma alteração faria antes de ela acontecer.
• Cole um manifesto para criar um novo recurso. Selecione vários recursos e exclua-os em lote com uma única confirmação.
• Scale, rollout restart, exclusão, cordon e drain. Um restart, um drain e uma exclusão só são executados após uma confirmação que nomeia o alvo, e uma exclusão em um cluster de produção sempre pede confirmação; nos demais, você pode desativar a confirmação de exclusão. O Scale pergunta primeiro o novo número de réplicas.

Ferramentas do Kubernetes, juntas
• Navegue por releases do Helm, valores, manifestos e histórico de revisões direto do cluster, sem precisar do binário do Helm.
• Navegue por Applications, ApplicationSets e AppProjects do Argo CD; faça sync ou refresh, ou encerre um sync em andamento, pela conexão com o Kubernetes, sem um segundo login.
• Revisão de acesso RBAC: o que posso fazer aqui, de onde vem o acesso de um ServiceAccount e quem pode fazer X?
• Ctrl+K encontra um comando; Ctrl+P troca de cluster; F1 mostra todos os atalhos. Temas claro e escuro incluídos.

Experimente antes de conectar
Escolha "Explore demo cluster" na primeira tela. O app inclui aplicações, Pods, logs e detalhes de recursos de exemplo, sem precisar de cluster, credenciais nem rede. Os dados de exemplo são identificados em todo lugar; exec, port-forward e a aplicação de YAML precisam de um cluster real. A interface do app é em inglês.

Seus clusters continuam seus
O kubeNimbus é um app NativeAOT autônomo, sem runtime do .NET para instalar. Ele se conecta apenas aos clusters que você abre, sem análise de uso, relatórios de falha nem verificação de atualizações próprios. A Microsoft Store cuida das atualizações do app. As credenciais ficam na sua cadeia de kubeconfig; a política de privacidade lista os arquivos locais de configuração e de sessão.

O kubeNimbus é gratuito e de código aberto sob a licença MIT: https://github.com/Shman4ik/kubeNimbus

Também do mesmo desenvolvedor: o pgNimbus, um cliente PostgreSQL feito do mesmo jeito, gratuito e na Store: https://apps.microsoft.com/detail/9N6SZT42XJ24
```

## Product features

One line per field.

```text
Abre em Applications: estado de saúde e uma explicação em uma linha para cada app
App desktop nativo: sem runtime do .NET para instalar
Sem telemetria, sem conta; credenciais nunca são armazenadas
Todos os tipos de recurso do cluster, inclusive recursos personalizados
Logs de Pods ao vivo: busca, regex, filtros por nível, salto para erros
Todos os Pods de um workload em um único fluxo de logs
Um terminal de verdade no contêiner (vi, top e mc funcionam)
Edição de YAML com server-side apply e prévia de dry-run
Scale, restart, exclusão e drain; exclusões em produção sempre pedem confirmação
Painéis de Service, Ingress e NetworkPolicy em linguagem simples
Sync do Argo CD e releases do Helm, lidos do cluster
Revisão de acesso RBAC: o que posso fazer, quem pode fazer X
Vários clusters ao mesmo tempo, cores prod/staging/dev, listas multicluster
Cluster de demonstração integrado: teste sem ter um cluster
Gratuito e de código aberto (MIT)
```

## Search terms

```text
kubernetes
k8s
kubectl
cliente kubernetes
kubernetes gui
logs de pod
helm
```

## Screenshot captions

In the order of the English listing.

```text
Applications: cada app com um veredito de saúde e uma explicação em uma linha
Enter para entender: os fatos por trás de um veredito e a última execução do Pod
Detalhe do Pod ancorado na parte inferior, com logs ao vivo
Edição de YAML com server-side apply
Uma lista única com vários clusters
O seletor de clusters, com cores de prod, staging e dev
Um terminal de verdade no contêiner
RBAC: quem pode fazer o quê
```
