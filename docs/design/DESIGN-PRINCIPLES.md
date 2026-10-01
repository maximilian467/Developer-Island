# Developer Island – Design Principles

Consolidated from the three binding sources, in priority order:

1. [`apple-design-analysis.md`](apple-design-analysis.md) (Apple design analysis, from [awesome-design-md](https://github.com/VoltAgent/awesome-design-md), MIT)
2. Developer Island product brief
3. Taste Skill `design-taste-frontend`
4. Vercel Web Interface Guidelines (`docs/design/web-interface-guidelines*.md`)
5. WinUI / Windows defaults

**Design read:** an always-on native desktop status capsule for developers, with an Apple-like, premium-consumer design language (Dynamic Island). It is built from a near-black capsule, the system type family and restrained spring motion.

**Dials:** DESIGN_VARIANCE 3 (a capsule is symmetric by nature) · MOTION_INTENSITY 6 · VISUAL_DENSITY 4.

---

## 1. Surfaces & color

The island is always dark, like the Dynamic Island. The Appearance setting (System / Dark) only changes the Settings window.

| Token | Value | Source / use |
|---|---|---|
| `IslandFill` | `#0A0A0B` | Near-black (Taste: no pure `#000`). Capsule body. |
| `IslandHairline` | `#FFFFFF` @ 9% | Vercel "crisp borders": a semi-transparent edge makes the capsule readable on dark wallpapers. |
| `SurfaceRaised` | `#FFFFFF` @ 6% | Inner wells (heat-map idle cells, secondary buttons). |
| `TextPrimary` | `#F5F5F7` | Apple parchment used as text on dark (Taste: no pure white). |
| `TextSecondary` | `#A1A1A6` | Secondary labels (≈ 7.9:1 on fill). |
| `TextTertiary` | `#7A7A7A` | Apple `ink-muted-48`: fine print only (≈ 4.6:1). |
| `Accent` | `#2997FF` | Apple Sky Link Blue, the on-dark variant of Action Blue. **The only accent.** It covers focus rings, progress, toggles, the heat map and the focus-state dot. |

Rules:

- One accent only. Claude and Codex are **not** color-coded. Labels identify them.
- No gradients, glow or neon. No glassmorphism on the island.
- Status dots appear only for real state: the running-focus indicator.
- Colour never carries meaning alone. Every state also has a text label.

## 2. Typography

Segoe UI Variable is the Windows counterpart of SF Pro (system-ui first, per DESIGN.md). Its optical sizes are *Display* for sizes of 20 and up, *Text* for 12–19 and *Small* for sizes below 12.

| Style | Size / weight | Tracking | Use |
|---|---|---|---|
| `Hero` | 44 / 600 Display | −0.28px | Focus countdown |
| `Figure` | 26 / 600 Display | −0.25px | Usage totals |
| `Title` | 15 / 600 Text | −0.2px | Track title, section heads |
| `Body` | 13 / 400 Text | −0.1px | Default copy inside the island |
| `Label` | 13 / 600 Text | −0.1px | Compact capsule values |
| `Caption` | 12 / 400 Text | 0 | Secondary lines |
| `Fine` | 11 / 400 Small | 0 | Disclaimers ("API equivalent") |

- The weight ladder is 400 / 600 only. No 500 and no 700 (DESIGN.md).
- Numbers use Segoe's default tabular figures, so timers never jitter.
- Use `…`, never `...`. No em or en dashes in visible copy (Taste 9.G). Use a hyphen or restructure.
- Use Title Case for buttons and sentence case elsewhere. Write short copy in the active voice.

## 3. Spacing & geometry

- The base unit is 4, and layout snaps to 8 / 12 / 16 / 20 / 24.
- **Radii (one documented grammar):**
  - Compact and medium capsules are full pills (radius = height / 2).
  - The expanded island has a radius of 28 and a content inset of 16, so inner media is 12 (nested radii: child = parent − inset).
  - Buttons and chips are pills; icon buttons are full circles.
  - Heat-map cells are 3.
  - Settings groups are 12 (they sit on a window, not in the island).
- Hit targets are at least 32 × 32 DIP for secondary actions and 44 for the primary play and start actions. Visual targets smaller than 24 get an expanded hit area.
- Optical alignment: glyph-based icons are nudged ±1 px where perception beats geometry (for example, the play triangle).

## 4. Elevation

- There is exactly one elevation device, the island's own shadow. It has two layers: an ambient layer (blur 24, 35%) and a key layer (blur 8, y 2, 30%). This follows Vercel's layered shadows; Apple reserves shadow for "objects resting on a surface", and the island is that object.
- Inside the island, depth comes only from surface tints, never from further shadows or cards.

## 5. Motion

- **Origin:** every morph grows from the anchor. A top-center island grows downward from the top-center point, and a bottom-right island grows up and to the left.
- **Shape morph:** a spring (damping ≈ 0.8, no visible bounce) on size, offset and corner radius, run entirely on the compositor.
- **Content:** outgoing content fades out in 120 ms and scales to 0.97. Incoming content waits 70 ms, then fades in over 240 ms and scales from 0.97 using the ease-out curve `(0.16, 1, 0.3, 1)`.
- **Press:** scale to 0.96 on pointer-down (DESIGN.md `scale(0.95)` micro-interaction).
- Every animation is interruptible and retargets from its current value.
- Motion is only used when it is motivated: state change, feedback or a new event. There are no idle loops and no perpetual animation.
- **Reduced motion:** when Windows *Animation effects* is off, morphs are instant and only a 100 ms opacity crossfade remains.

## 6. Interaction & accessibility

- Every interactive element is a real `Button` or `ToggleSwitch` with an `AutomationProperties.Name`. Icon-only buttons are always named.
- Focus rings are visible (2 px accent, radius-matched) and appear on keyboard focus only.
- Keyboard:
  - Esc collapses the island.
  - Tab and Shift+Tab move through the expanded controls.
  - Arrow keys move between tabs.
  - Space toggles Play.
- Interactive states increase contrast: hover raises the fill by 6–8%, and pressed scales the element.
- **Focus management:** expanding activates the island. Collapsing returns focus to the window that had it before.
- Transient activities (song change, focus finished) never steal focus.
- Every state is designed: empty ("No media playing"), unavailable ("Not detected") and error ("Couldn't read Codex logs").

## 7. Copy

- "API equivalent" / "Estimated API value" are used for costs, never "cost", "spent" or "bill".
- Values that are not available read "Unavailable" or "Not detected". Values are never invented.
- Counts use numerals, and numbers and units are separated by a non-breaking space ("25 min", "1.2M tokens").

## 8. Anti-slop checklist (applied at every audit)

- [ ] No em or en dashes in visible text
- [ ] One accent, one radius grammar, one icon family (Segoe Fluent Icons)
- [ ] No cards inside the island; grouping uses spacing
- [ ] No decorative dots, no eyebrows, no gradients, no glow
- [ ] Contrast of at least 4.5:1 for all text
- [ ] Every animation is motivated and interruptible, and has a reduced-motion path
- [ ] Empty, unavailable and error states exist for every module
- [ ] Hit targets of at least 32, and named icon buttons
