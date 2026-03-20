# AuraClean — Design Task

## Design Context

### Users

**Primary audience:** Mainstream Windows users who want a single trusted app to understand, clean, and maintain their PC — without needing to know what "WinSxS" means or feeling at risk of breaking something. They want clarity and confidence, not complexity.

**Secondary audience:** Power users who expect access to deeper tools — startup control, storage analysis, installer diffing, file shredding, threat scanning — without the app getting in their way.

**Product fantasy:** *"I want one trusted app that helps me understand, clean, and maintain my Windows PC without making me feel dumb or at risk."*

**UX implication:** Approachable on the surface, advanced underneath. Progressive disclosure is the core interaction model — start simple, reveal depth through interaction. The first experience should feel guided and safe. Power features should be accessible but never overwhelming on first contact.

---

### Brand Personality

**Three words:** Precise. Trustworthy. Powerful.

**Voice & tone:**
- Professional and calm — never alarmist, never salesy
- Confident but not arrogant — the app knows what it's doing
- Direct — every word earns its place
- Reassuring — "we've got this" energy, not "YOUR PC IS IN DANGER" energy

**Emotional goals:**
- Mainstream users should feel: safe, informed, in control
- Power users should feel: respected, efficient, unblocked
- Nobody should feel: anxious, confused, or suspicious of the app's motives

**Reference touchstones** (for feel, not imitation):
- **1Password** — trust, polish, premium care
- **Linear** — clarity, sharpness, purposeful density
- **CleanMyMac** — guided utility flow, visual friendliness
- **Windows Security** — serious but not scary, institutional confidence

---

### Anti-References — What AuraClean Must NOT Be

- **Not gamery** — no RGB aesthetics, no "performance scores" framed as leaderboards
- **Not crypto/cyberpunk** — no fake-hacker terminal vibes, no neon-on-void
- **Not scamware** — no "BOOST YOUR PC 3000%" energy, no countdown timers, no scare tactics
- **Not enterprise-gray** — no corporate blandness, no SharePoint energy
- **Not AI slop** — no purple-to-cyan gradients, no glowing buttons, no glassmorphism everywhere, no "looks like it's selling a webinar"

---

### Aesthetic Direction

**Label:** Refined minimal with industrial precision.

**What that means in practice:**
- More "modern desktop pro tool" than "design concept on Dribbble"
- Premium utility — the visual equivalent of a well-made tool, not a showroom concept
- Minimal but not sterile — warmth through considered details, not decorative excess
- Grounded and trustworthy — the UI must earn the right to request admin privileges
- The name "Aura" provides the aspirational edge; the interface provides the professional foundation

**Theme commitment:** Dark mode is the primary and polished experience. Light mode is not a priority unless it can be rebuilt properly — one excellent dark theme beats two inconsistent ones.

**Palette direction:**
- Full rethink from the current violet-cyan-on-black "neon sci-fi" palette
- **Violet preserved** as a restrained signature accent — identity marker, not personality replacement
- Cyan earns its place only as a functional accent (success states, active indicators) — not a co-lead
- Move toward muted, grounded neutrals with selective color emphasis
- Tinted dark backgrounds (warm or cool) instead of pure black void
- Strong contrast for text and interactive elements — no style over legibility

---

### Design Principles

These five principles should guide every design decision in AuraClean:

**1. Trust over flash.**
Every visual choice must build credibility. A system utility that handles files, registry, and admin-level operations earns trust through restraint, clarity, and professional craft — not glow effects and gradient text. If a design element makes the app look like it's trying too hard, remove it.

**2. Progressive disclosure, not progressive overload.**
The app has 20+ features spanning cleanup, analysis, security, and tools. Surface the right 3–4 actions for mainstream users. Reveal depth through interaction — expandable sections, mode-based navigation, smart grouping. The sidebar should feel manageable, not exhaustive. A user should never feel "this app does too much for me."

**3. Every element earns its place.**
No decorative rings. No sparklines that convey nothing. No icon badges above every heading. No identical card grids repeated across 8 views. If a visual element doesn't inform, orient, or enable action — it's noise. Ruthlessly audit every pixel.

**4. Motion with purpose, not motion for personality.**
The current app has zero animation — views swap via instant visibility toggle. The fix isn't "add bounce effects everywhere." It's purposeful motion: staggered content reveals on page entry, smooth state transitions, feedback on interaction. Every animation should answer "what changed?" not "isn't this cool?"

**5. Hierarchy solves complexity.**
When everything is primary, nothing is. One clear action per context. Button hierarchy enforced through style weight (primary, secondary, ghost — not three primaries in a row). Typography scale formalized. Spacing creates rhythm (tight groupings, generous separations) instead of uniform padding everywhere.

---

### Current State Assessment

**What works (preserve):**
- Sidebar navigation structure and feature grouping logic (cleanup, analyze, tools)
- MVVM architecture and design token system in App.xaml
- Centralized converter registration
- Runtime theme switching infrastructure (ThemeService)
- Logical information architecture across views

**What must change:**
| Area | Current | Target |
|---|---|---|
| **Palette** | Violet-cyan-on-black (#080810), 4 competing accents, purple glow effects | Grounded dark neutrals, violet as restrained accent, functional color only |
| **Typography** | Roboto (MaterialDesign default), 17 ad-hoc sizes, no type scale | Distinctive font pairing, formal modular type scale, clear hierarchy |
| **Spacing** | Uniform 20px padding, 32px margins everywhere, no rhythm | Varied spacing with intentional rhythm — tight groups, generous separations |
| **Cards** | Identical 14px radius + 20px padding across all views, same stat-card template x8 | Purposeful container variety — some content needs cards, some doesn't |
| **Buttons** | 6 primary buttons competing on dashboard, ALL-CAPS text | Clear hierarchy: one primary per context, secondary/ghost for rest |
| **Motion** | Zero — instant view swaps, no transitions | Purposeful entrance animations, state transitions, interaction feedback |
| **Navigation** | 20 flat items always visible | Grouped/collapsible sections, 4–6 top-level destinations |
| **Accessibility** | Score 2/10 — no focus states, no automation properties, keyboard gaps | WCAG AA minimum, focus indicators, keyboard nav, reduced motion, no color-only meaning |
| **Light mode** | Broken — 40+ hardcoded hex values bypass theme system | Deferred — dark mode excellence first |

---

### Technical Constraints

- **Framework:** .NET 8 WPF (no migration planned)
- **UI library:** MaterialDesign XAML Toolkit (available but should not dictate aesthetic)
- **MVVM:** CommunityToolkit.Mvvm (source generators)
- **Window:** Custom chrome, 1280×780 default, 1050×650 minimum
- **Deployment:** Self-contained single-file EXE (~73 MB), requires Administrator
- **Platform:** Windows 10/11 x64 only
