# Design audit — V1 continuation

Reviewed 2026-09-28. This records the current source review and targeted runtime inspection, not a claim that every earlier implementation phase was independently audited.

## Sources and scope

Priority remains [the local Apple analysis](../../vorlage/DESIGN.md), then [design-taste-frontend](../../.agents/skills/design-taste-frontend/SKILL.md), then [Web Interface Guidelines](https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md). Their native adaptation is documented in [DESIGN-PRINCIPLES.md](DESIGN-PRINCIPLES.md). Web-only requirements such as HTML semantics, URL navigation and browser autofill do not apply literally to WinUI.

No layout, stack, brand or feature redesign was introduced. Existing capsule geometry, typography, near-black surfaces, restrained blue accent, shadows and compositor motion were retained.

| Area | Review and result |
|---|---|
| Hierarchy | Compact status, temporary activity, expanded detail remain distinct. Only selected/available module content is shown; disabling every module no longer leaves an old panel beneath the empty state. |
| Spacing and alignment | Existing 16 DIP expanded inset, 28 DIP expanded radius and settings row alignment retained. Settings Modules was inspected live at its normal size; no clipping observed there. |
| Typography | Segoe UI Variable, regular/semibold weights, existing restrained size scale retained. Primary information has stronger contrast than secondary metadata. |
| Color | Near-black island and single blue accent retained. Primary Start button now changes its text to the light foreground on dark hover/pressed fills, avoiding dark-on-dark text. |
| Radius/shadow/blur | Capsule radii and two-layer shadow assets preserved. No extra glass, gradients or decorative effects added. Native hit region includes a rectangular shadow margin. |
| Motion | Existing interruptible compositor spring and main reduced-motion path retained. Not all small XAML hover/press transitions are disabled by reduced motion; this remains a refinement. |
| Accessible names | Existing transport/timer controls have names. The five Settings navigation buttons now have explicit AutomationProperties.Name; their composed icon/text content previously produced unnamed button nodes. |
| Keyboard | Existing Escape handling, tab navigation, focus restoration and arrow-based graph inspection retained. Full Narrator and keyboard-only acceptance remain open. |
| Honest data | Missing provider data remains explicit. Unknown pricing now propagates into history so incomplete monetary values are not presented as complete estimates. |
| Demo separation | Marketing screenshots use demo data. Production chooses real providers and never substitutes mock percentages. |

## Remaining visual and accessibility checks

- Full Narrator, Windows high-contrast and enlarged text-scale testing.
- All six anchors, snapping, monitor removal/reconnection and mixed-DPI checks on the final package. Earlier handoff reports are not substituted for fresh verification.
- Very narrow Settings windows and long localized/provider text may need adaptive layout work; no broad responsive redesign was included.
- Media timeline hit-target size and every reduced-motion control transition need a dedicated accessibility pass.
- Inspect fullscreen transitions with real games/video apps and keyboard focus restoration across applications.

Build, runtime and performance checks are recorded in [PERFORMANCE.md](../PERFORMANCE.md) and in the phase audits below.

## Phase audits during the initial implementation

Each UI phase was checked against the Apple analysis, the Taste Skill and the Web Interface Guidelines. The checks used screenshots and frame bursts of the running app in demo and normal mode (`tools/screenshot.ps1`, `tools/burst.ps1`). The findings below were fixed before the phase was considered done.

### Compact island

| Kind | Finding | Fix |
|---|---|---|
| Visual | The capsule rendered as an opaque black rectangle. `LayerVisual` shadows are opaque on a transparent window. | Nine-grid shadow texture (`Assets/island-shadow.png`) instead of `LayerVisual`/`DropShadow`. |
| Visual | A black 500 × 480 frame flashed at start-up before the first layout. | Empty window region until the first layout. |
| Interaction | A release after a drag also counted as a click and expanded the island. | Read the drag state before releasing pointer capture. |
| Accessibility | The compact island has no keyboard entry point. | The tray icon (Win+B, then Enter) expands it with keyboard focus inside. The accessible name covers every value shown. |
| Motion | The morph spring settled in about 150 ms, which felt abrupt. | Damping 0.84, period 80 ms (about 250 ms, no visible overshoot). |

### Expanded island

| Kind | Finding | Fix |
|---|---|---|
| Visual | 1 px white vertical lines appeared at the window edges after activation (the Windows 11 active border). | Region kept 4 DIP inside the window; the border colour is suppressed on activation. |
| Interaction | Each tab was its own Tab stop. | Tab strip is one stop (`TabFocusNavigation="Once"`); arrow keys switch tabs. |
| Accessibility | Tooltips on icon buttons are popups that the window region can clip. | Tooltips removed; every icon button keeps an `AutomationProperties.Name`. |
| Spacing | Inner media radius was not concentric with the island radius. | 28 DIP outer radius, 16 DIP inset, 12 DIP artwork; the activity's leading visual sits inside the capsule curve. |

### Music

| Kind | Finding | Fix |
|---|---|---|
| Visual | The song-change activity showed the previous track's artwork, because artwork decodes asynchronously. | `ArtChanged` updates the visible activity. |
| Interaction | The first snapshot after launch looked like a song change. | The initial snapshot is primed silently. |
| Accessibility | The timeline focus ring had square corners. | Rounded ring (radius 8) with a margin; Left and Right arrows seek 5 s. |

### AI usage

| Kind | Finding | Fix |
|---|---|---|
| Visual | "0 tokens, 0,00 € API equivalent" for an unused provider was noise. | The value line is hidden without usage; "No sessions today" remains. |
| Honesty | Models without a list price (for example `gpt-6-astra`) must not be guessed. | Unpriced tokens mark values as partial or unavailable. |

### Focus

| Kind | Finding | Fix |
|---|---|---|
| Copy | The header read "Focus Session" and "50 min session" side by side, which repeats itself. | "Focus" followed by "50 min session". |
| History | Only today was shown. | Added a line for the last seven days. |

### Settings

| Kind | Finding | Fix |
|---|---|---|
| Visual | Toggles floated about 150 px from the card edge, because WinUI reserves a label column. | Label-less toggles are pinned to the switch width. |
| Visual | The theme radio buttons used the Windows accent (salmon on the test PC). | Accent resources overridden (Action Blue light, Sky Blue dark); the theme picker is now a segmented control. |
| Bug | `ToggleSwitchPreContentMargin` was set as a Thickness, but WinUI expects a Double, which broke rendering. | Corrected the resource type. |
| Visual | The anchor picker pills were clipped by RadioButton's default MinWidth of 120. | MinWidth 0. |

### Pre-release checks

- No en or em dashes or three-dot ellipses in user-visible strings, and weights are 400 and 600 only (mechanical scan of `src/`).
- One accent colour and one icon family (Segoe Fluent Icons). There are no eyebrows, no gradients and no decorative dots; the dots mark live state only.
- Contrast is at least 4.5:1 for all text, including tertiary text (#7A7A7A on #0A0A0B, about 4.6:1).
- Transparent areas pass clicks through, verified with `WindowFromPoint` outside the capsule.
- Real-data runs:
  - Claude Code transcripts and Codex rollouts parsed correctly.
  - A Media Player SMTC session, including the switch to the next session when it closed.
  - Autostart on and off, verified in the Run key.
  - Second-monitor placement at Bottom Right, with the DPI rescale fixed.
  - A missing-monitor fallback to Top Center on the primary monitor.
