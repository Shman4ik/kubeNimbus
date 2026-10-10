# What the list search matches, kind by kind

UI rule 13 in `CLAUDE.md` gives the list its own search box and says it matches what
*identifies* an object. This page is the table behind that sentence, and
`src/KubeNimbus.App/ViewModels/RowFilterFields.cs` is the same table as code; a change to
one is a change to the other in the same PR. Read this before changing
`ResourceRowViewModel.Matches`, `RowFilterFields` or `ClusterTabViewModel.MatchesRowFilter`.

## The rule

A row matches when the query is a case-insensitive substring of one of its **identity
fields**: the fields a person uses to name the object they are looking for. Never a status, a
condition or a count, because "Running", "True" or "1" would match most of a healthy list and
the search would stop narrowing anything.

| Kind | Identity fields | Why |
|---|---|---|
| every kind | name, namespace; the cluster in a fleet list | What names any object. The cluster only where it is a column. |
| Event (core and `events.k8s.io`) | reason, object, message | Its own name is a generated `<object>.<hex>` nobody types; what happened, to what, and the sentence it logged are how an event is found ([events-list](events-list.md)). Type stays out: "Normal" matches most of the list. |
| Ingress (`networking.k8s.io`) | every `spec.rules[].host` | An Ingress is known by the hostname it serves: "which Ingress serves shop.example.com?" (FEAT-65, the owner's decision of 2026-10-10). Not its load balancer address, which is status, and not its class. |

Not in the table, on purpose:

- **A CRD's printer cells.** They are the same kind of content as a status ("True",
  "Ready", "1.15.2"), and matching them would change what the box does from kind to kind
  with no rule a reader could learn. This was the "cheapest honest version" FEAT-65's
  backlog row proposed, and the owner chose the table instead.
- **An Ingress's TLS-only hosts** (`spec.tls[].hosts` with no rule). They name a certificate,
  not a route the Ingress serves.

Next candidates, when someone asks: a Gateway API route's `spec.hostnames` and a Gateway
listener's `hostname`, which identify those kinds the way a host identifies an Ingress.

## How it is wired

`RowFilterFields.KindFields` reads the extra fields from the object whenever its row is built
or updated (`ResourceRowViewModel.Update`), so a watch Modified that changes a host changes
what matches, and `RowFilterFields.Matches` is the predicate `ResourceRowViewModel.Matches`
delegates to. The kind is read from the object's own `apiVersion` and `kind`, group included,
so a CRD that happens to be called `Ingress` gets nothing from the table.

`ResourceRowMatchesTests` pins the table: the matches and, as importantly, the non-matches
(status, address, class, Type, a lookalike CRD). Removing the extra-field step turns
`An_ingress_matches_every_host_its_rules_name_and_not_its_address_or_class` red (checked).
