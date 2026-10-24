# Keyboard shortcuts

<!-- Generated from KubeNimbus.Core.Commands.CommandCatalog by ShortcutDocs.ToMarkdown(). Do not edit by hand — run the Core tests with KUBENIMBUS_UPDATE_DOCS=1 to regenerate. -->

Every shortcut below is also discoverable in the app: press <kbd>F1</kbd> for the
cheat sheet, or <kbd>Ctrl</kbd>+<kbd>K</kbd> to search commands by name.

The **Windows / Linux** column is the Ctrl scheme and the **macOS** column the Cmd
scheme, spelled the way each platform writes a shortcut (Ctrl+Shift+N, ⇧⌘N); the Cmd
scheme also adds a few Mac conventions (⌘⌫ to delete, ⌘[ to go back). The scheme
follows the platform by default and can be forced either way in Preferences → General
→ Shortcut modifier.

## Clusters

| Action | Windows / Linux | macOS |
| --- | --- | --- |
| Switch or open a cluster | Ctrl+P | ⌘P |
| Jump to cluster tab 1–9 | Ctrl+1 … Ctrl+9 | ⌘1 … ⌘9 |
| New cluster tab | Ctrl+T | ⌘T |
| Close cluster tab | Ctrl+W | ⌘W |
| Correct a cluster's environment colour | Right-click a cluster tab | Right-click a cluster tab |
| Reorder cluster tabs | Drag tab | Drag tab |

## Resources

| Action | Windows / Linux | macOS |
| --- | --- | --- |
| Choose namespace | Ctrl+Shift+N | ⇧⌘N |
| Search the resource list by name | Ctrl+F | ⌘F |
| Filter the sidebar's resource kinds | Type in the filter box | Type in the filter box |
| Open the selected resource | Enter | ↩ |
| Quick-peek the selected resource | Space | Space |
| Default action (pod → logs, resource → YAML, …) | Double-click | Double-click |
| Logs, exec, port-forward, YAML, delete | Right-click a row | Right-click a row |
| Edit YAML | E | E |
| Delete the selected resource (asks to confirm) | Delete | ⌘⌫ / ⌦ |
| Rollout restart the selected workload (asks to confirm) | R | R |
| Search the list from the rows | / | / |
| Show only what is unhealthy (warnings and errors), with the rows focused; in a text box Ctrl+Z is Undo | Ctrl+Z | ⌃Z |
| Refresh the list | Ctrl+R | ⌘R |
| Review, then apply, the edited YAML | Ctrl+S | ⌘S |

## Pods

| Action | Windows / Linux | macOS |
| --- | --- | --- |
| Logs — a pod's, a workload's, or an event's pod; also in detail panes | L | L |
| Logs, opened full-size over the list (Esc returns to split) | Shift+L / Shift+click a row's logs icon | ⇧L / Shift+click a row's logs icon |
| Logs of any pod or workload | Ctrl+Shift+L | ⇧⌘L |
| Previous logs (crashed container) | P | P |
| Shell into the pod — or scale a Deployment/StatefulSet | S | S |
| Port-forward | F | F |
| Interrupt a command in the exec pane | Ctrl+C | ⌃C |
| End input / exit the shell | Ctrl+D | ⌃D |
| Complete a command in the exec pane | Tab | ⇥ |
| Copy the terminal selection | Ctrl+Shift+C | ⌃⇧C |
| Paste into the terminal | Ctrl+Shift+V | ⌃⇧V |
| Follow the log stream | The Follow toggle, above the log pane | The Follow toggle, above the log pane |
| Step through a log search's matches (the funnel filters instead) | Enter / Shift+Enter in a log pane's search box | ↩ / ⇧↩ in a log pane's search box |
| Search a log with a regular expression or matching case, or pin the search as a highlight (!word hides lines) | Alt+R / Alt+C / Alt+P in a log pane's search box | ⌥R / ⌥C / ⌥P in a log pane's search box |
| Jump to the latest error in a log, then the ones before it | Alt+↑ / Alt+↓ in a log pane, or click its error count | ⌥↑ / ⌥↓ in a log pane, or click its error count |
| Move to a line in a focused log pane, and open or close a JSON line's fields | ↑ / ↓, Home / End in a focused log pane; Enter opens a JSON line | ↑ / ↓, ↖ / ↘ in a focused log pane; ↩ opens a JSON line |
| Copy the log line the keys are on | Ctrl+C | ⌘C |

## View & app

| Action | Windows / Linux | macOS |
| --- | --- | --- |
| Show the Applications list: every app's health and why | Ctrl+Shift+A | ⇧⌘A |
| Show the Resources explorer: kinds, lists and the inspector | Ctrl+Shift+R | ⇧⌘R |
| Back from an application's page to the list | Esc | ⎋ / ⌘[ |
| Open the command palette | Ctrl+K | ⌘K |
| Show every resource kind in the sidebar, or only the everyday ones | The eye-plus icon, top of the sidebar | The eye-plus icon, top of the sidebar |
| Show or hide the resource sidebar | Ctrl+B | ⌘B |
| Maximize the inspector over the list | The inspector's expand icon; Esc restores | The inspector's expand icon; Esc restores |
| Close the focused cluster tab or dock tab | Delete | ⌫ / ⌦ |
| Move focus out of the YAML editor or the terminal | Ctrl+Tab / Ctrl+Shift+Tab | ⌃⇥ / ⌃⇧⇥ |
| Preferences… | Ctrl+, | ⌘, |
| Show this cheat sheet | F1 | F1 |

