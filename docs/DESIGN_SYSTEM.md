# AuraClean Design System

## Architecture

```
App.xaml                          ← slim merger + converter registrations
├── MaterialDesign theme
├── Resources/DesignTokens.xaml   ← all primitive values
└── Resources/ComponentStyles.xaml ← reusable component styles
```

**App.xaml** merges the two dictionaries and registers global converters. All design tokens and component styles live in their own files.

---

## Design Tokens (`Resources/DesignTokens.xaml`)

### Color Primitives

| Key | Value | Role |
|-----|-------|------|
| `AuraBgColor` | `#080810` | App background |
| `AuraSurfaceColor` | `#0F0F1E` | Card/panel background |
| `AuraSurfaceLightColor` | `#171730` | Elevated surface |
| `AuraSurfaceElevatedColor` | `#1F1F42` | Highest elevation |
| `AuraVioletColor` | `#7C5CFC` | Primary accent |
| `AuraCyanColor` | `#00E5C3` | Secondary accent |
| `AuraCoralColor` | `#FF6B8A` | Warning |
| `AuraMintColor` | `#5BF0D7` | Success |
| `AuraAmberColor` | `#FFB74D` | Caution |
| `AuraCriticalColor` | `#FF4444` | Critical/error |
| `AuraBlueColor` | `#64B5F6` | Info |
| `AuraTextBrightColor` | `#F0F0FF` | Headings |
| `AuraTextColor` | `#C8C8E0` | Body text |
| `AuraTextDimColor` | `#7B7BA0` | Secondary text |
| `AuraTextMutedColor` | `#3D3D5C` | Disabled/labels |
| `AuraBorderColor` | `#222244` | Standard border |
| `AuraBorderSubtleColor` | `#1A1A36` | Subtle border |

### Semantic Brushes

Consume these in views — never use Color primitives directly.

| Key | Maps to | Purpose |
|-----|---------|---------|
| `AuraBackground` | `AuraBgColor` | Page background |
| `AuraSurface` | `AuraSurfaceColor` | Card background |
| `AuraSurfaceLight` | `AuraSurfaceLightColor` | Elevated panels |
| `AuraSurfaceElevated` | `AuraSurfaceElevatedColor` | Highest elevation |
| `AuraAccentPurple` | `AuraVioletColor` | Primary actions |
| `AuraAccentTeal` | `AuraCyanColor` | Secondary actions |
| `AuraWarning` | `AuraCoralColor` | Warning states |
| `AuraSuccess` | `AuraMintColor` | Success states |
| `AuraAmber` | `AuraAmberColor` | Caution |
| `AuraCritical` | `AuraCriticalColor` | Error/critical |
| `AuraInfo` | `AuraBlueColor` | Information |
| `AuraTextBright` | `AuraTextBrightColor` | Headings, emphasis |
| `AuraTextPrimary` | `AuraTextColor` | Body content |
| `AuraTextSecondary` | `AuraTextDimColor` | Labels, secondary |
| `AuraTextMuted` | `AuraTextMutedColor` | Disabled, hints |
| `AuraBorder` | `AuraBorderColor` | Default borders |
| `AuraBorderSubtle` | `AuraBorderSubtleColor` | Light separators |

### Gradient Brushes

| Key | Direction | Colors |
|-----|-----------|--------|
| `AuraGradientAccent` | Diagonal ↘ | Violet → Cyan |
| `AuraGradientVertical` | Top → Bottom | Violet → Cyan |
| `AuraGradientHorizontal` | Left → Right | Violet → Cyan |
| `AuraGradientWarm` | Left → Right | Coral → Amber |
| `AuraSidebarGradient` | Top → Bottom | `#0C0C1A` → `#08080F` |

### Overlay & Badge Brushes

**Overlays** (semi-transparent layers for interactive states):
`AuraAccentPurpleSemi`, `AuraAccentTealSemi`, `AuraAmberSemi`, `AuraCoralSemi`, `AuraOverlayLight`, `AuraOverlaySubtle`, `AuraOverlayFaint`, `AuraTrackBg`

**Badges** (~8% alpha accent tints):
`AuraAccentPurpleBadge`, `AuraAccentTealBadge`, `AuraAmberBadge`, `AuraBlueBadge`

**Severity** (~20% alpha for status backgrounds):
`AuraCriticalSeverity`, `AuraCoralSeverity`, `AuraAmberSeverity`, `AuraSuccessSeverity`

### Typography Scale

| Token | Size | Use case |
|-------|------|----------|
| `AuraFontSizeXS` | 10 | Badges, metadata |
| `AuraFontSizeCaption` | 11 | Status, timestamps |
| `AuraFontSizeLabel` | 12 | Form labels, secondary info |
| `AuraFontSizeBody` | 13 | Primary body text |
| `AuraFontSizeMD` | 14 | Sub-headings, button text |
| `AuraFontSizeLG` | 15 | Section headings |
| `AuraFontSizeXL` | 18 | Secondary page titles |
| `AuraFontSizeTitle` | 22 | Page titles |
| `AuraFontSizeDisplay` | 26 | Page headings |
| `AuraFontSizeHero` | 42 | Large stat numbers |
| `AuraFontSizeJumbo` | 60 | Focal-point metric |

```xml
<!-- Usage -->
<TextBlock FontSize="{StaticResource AuraFontSizeBody}" />
```

### Spacing Scale

**Uniform (all sides):**

| Token | Value | Use case |
|-------|-------|----------|
| `AuraSpaceXS` | 4 | Micro gap, icon-to-label |
| `AuraSpaceS` | 8 | Between related items |
| `AuraSpaceM` | 12 | Grouped elements |
| `AuraSpaceL` | 16 | Card padding, sections |
| `AuraSpaceXL` | 20 | Between sections |
| `AuraSpace2XL` | 24 | Major separations |
| `AuraSpace3XL` | 32 | Page-level margins |

**Directional variants:** `AuraSpaceBottom{XS/S/M/L/XL/2XL}`, `AuraSpaceTop{XS/S/M}`, `AuraSpaceRight{M/L}`

**Page:** `AuraPageMargin` = `32,24,32,16`

```xml
<!-- Usage -->
<Border Margin="{StaticResource AuraSpaceBottomL}" />
```

### Radius Scale

| Token | Value | Use case |
|-------|-------|----------|
| `AuraRadiusXS` | 4 | Tiny badges |
| `AuraRadiusSM` | 6 | Chips |
| `AuraRadiusBase` | 8 | Compact cards |
| `AuraRadiusM` | 10 | Buttons, nav items |
| `AuraRadiusL` | 12 | Standard cards |
| `AuraRadiusXL` | 14 | Large cards |
| `AuraRadius2XL` | 16 | Feature cards |
| `AuraRadiusFull` | 999 | Pills, circles |

```xml
<!-- Usage -->
<Border CornerRadius="{StaticResource AuraRadiusM}" />
```

### Layout Constants

| Token | Value | Purpose |
|-------|-------|---------|
| `AuraSidebarWidth` | 260 | Sidebar panel width |
| `AuraIconBadgeSize` | 44 | Icon badge container |
| `AuraIconBadgeIconSize` | 22 | Icon inside badge |
| `AuraNavButtonHeight` | 42 | Nav button height |

---

## Component Styles (`Resources/ComponentStyles.xaml`)

### Buttons

| Style Key | Target | Visual |
|-----------|--------|--------|
| `AuraPrimaryButton` | `Button` | Violet gradient fill, white text, glow shadow |
| `AuraSecondaryButton` | `Button` | Teal solid fill, white text |
| `AuraOutlinedButton` | `Button` | Transparent + teal border, teal text |
| `AuraGhostButton` | `Button` | Transparent + subtle border, secondary text |

All buttons include `IsMouseOver` (opacity dim) and `IsEnabled=False` (40% opacity) triggers.

```xml
<Button Style="{StaticResource AuraPrimaryButton}" Content="Scan Now" />
```

### Cards

| Style Key | Target | Padding | Radius | Background |
|-----------|--------|---------|--------|------------|
| `AuraGlassCard` | `Border` | 20 | XL (14) | Surface |
| `AuraGlassCardCompact` | `Border` | 14,8 | M (10) | SurfaceLight |
| `AuraGlassCardFlush` | `Border` | 0 | XL (14) | Surface |
| `AuraSectionCard` | `Border` | 20 | XL (14) | Surface + bottom margin |

```xml
<Border Style="{StaticResource AuraGlassCard}">
    <!-- card content -->
</Border>
```

### Navigation

| Style Key | Target | Purpose |
|-----------|--------|---------|
| `AuraNavButton` | `RadioButton` | Sidebar nav item with gradient active bar |

### Text Styles

| Style Key | Size | Weight | Color |
|-----------|------|--------|-------|
| `AuraPageTitle` | Display (26) | Light | TextBright |
| `AuraPageSubtitle` | Body (13) | Normal | TextSecondary |
| `AuraSectionTitle` | LG (15) | SemiBold | TextBright |
| `AuraLabel` | Label (12) | Normal | TextSecondary |
| `AuraBodyText` | Body (13) | Normal | TextPrimary |
| `AuraStatValue` | Title (22) | SemiBold | TextBright |
| `AuraMutedText` | Caption (11) | Normal | TextMuted |
| `AuraNavSectionLabel` | XS (10) | SemiBold | TextMuted |

```xml
<TextBlock Style="{StaticResource AuraPageTitle}" Text="Dashboard" />
```

### Inputs & Badges

| Style Key | Target | Purpose |
|-----------|--------|---------|
| `AuraSearchBox` | `TextBox` | Rounded search input |
| `AuraStatusBadge` | `Border` | Compact status label |
| `AuraChip` | `Border` | Category/tag chip |

---

## Theme Switching

`ThemeService.cs` swaps Color and SolidColorBrush resources at runtime. It keys on the same resource names defined in `DesignTokens.xaml`. Currently theme-aware keys:

- `AuraBgColor` / `AuraBackground`
- `AuraSurfaceColor` / `AuraSurface`
- `AuraSurfaceLightColor` / `AuraSurfaceLight`
- `AuraSurfaceElevatedColor` / `AuraSurfaceElevated`
- `AuraTextBrightColor` / `AuraTextBright`
- `AuraTextColor` / `AuraTextPrimary`
- `AuraTextDimColor` / `AuraTextSecondary`
- `AuraTextMutedColor` / `AuraTextMuted`
- `AuraBorderColor` / `AuraBorder`
- `AuraBorderSubtleColor` / `AuraBorderSubtle`

Accent colors, overlays, badges, and severity brushes are **not** theme-switched.

---

## Global Converters (in App.xaml)

| Key | Class | Purpose |
|-----|-------|---------|
| `InverseBoolConverter` | `InverseBoolConverter` | `!bool` |
| `FileSizeConverter` | `FileSizeConverter` | Bytes → human-readable |
| `BoolToVis` | `BoolToVisibilityConverter` | `bool` → Visibility |
| `InverseBoolToVis` | `InverseBoolToVisibilityConverter` | `!bool` → Visibility |
| `IntToVisibilityConverter` | `IntToVisibilityConverter` | `int > 0` → Visible |
| `TreemapColorConverter` | `TreemapColorConverter` | Size → treemap color |
| `HexToBrush` | `HexToBrushConverter` | Hex string → Brush |
| `ScoreToWidth` | `ScoreToWidthConverter` | Score → bar width |
| `HealthColorConverter` | `HealthScoreColorConverter` | Health → status color |
