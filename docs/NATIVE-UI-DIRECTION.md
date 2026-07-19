# Native UI direction

LFTP Pilot is the native Windows successor to LFTP Commander. Commander is the
behavioral reference for workflow clarity, not a visual template or a parity
checklist. The native application keeps the parts that make a two-pane transfer
client immediately legible and uses WinUI where Windows can now do better.

## What the Commander review established

| Workflow | Commander strength | Native direction |
| --- | --- | --- |
| Connection | The active site, connect action, and state are always visible. | Integrate session tabs into the title bar, use one subtle state dot and one close affordance, and lead with Connections instead of a flat advanced-feature toolbar. |
| File panes | Compact title, path, tools, columns, rows, and selection status form one uninterrupted surface. | Use the same information hierarchy with WinUI buttons, menus, virtualization, Explorer drag/drop, and system accessibility. |
| Transfer direction | GET and PUT sit between the source and destination panes, with a small readiness indicator. | Preserve that spatial model. Transfer options and recursive search move behind one small session-tools menu. |
| Activity | Transfers, history, and log share a resizable bottom dock. | Use a full-width native Activity expander with a single flat header, compact view selectors, and active-job count. Do not nest a card or second window surface inside the dock. |
| Settings | A left section list separates transfers, behavior, notifications, engine, advanced, and storage. | Use native navigation for only real settings: interface, transfer defaults, authenticated engine facts, storage, diagnostics, and updates. Do not recreate obsolete font/OLED/runtime-path controls. |
| Site manager | Saved sites remain visible beside one focused editor and pinned Save/Connect actions. | Retain that layout, add descriptive protocol/authentication labels, and keep host-key review inside the connection task. |

## Product rules

1. The dual panes remain the dominant surface at every window size supported by
   the app.
2. Common file actions stay adjacent to the pane they affect. Advanced LFTP
   capabilities remain available without competing with browsing.
3. Empty, loading, disconnected, and failed states occupy the affected pane;
   they do not erase the workspace or obscure unrelated local work.
4. Settings expose controls only when the value is persisted and applied.
   Security and runtime-integrity policy is described, not presented as a fake
   toggle.
5. Native improvements remain intentional: persistent tabs, Windows theme and
   high contrast, Explorer drag/drop, Mica, App Installer updates, Agent-owned
   background work, notifications, taskbar progress, and Jump Lists.
6. LFTP Commander data and repository history remain outside the product. The
   old app is evidence, not a migration source.
7. Shell geometry is deliberate: a 40-pixel title/session strip, 8-pixel outer
   gutters, 30-pixel pane commands, compact file rows, and a 52-pixel transfer
   spine. Controls align to pane and dock edges instead of floating in unused
   space.
8. Connection state appears once in the session tab. Session closure also
   appears once; extra glyphs or detached status pills are regressions.
