# Companion concepts

Presentation only: names, roles and look briefs for the rows in `content_src/companions.csv`. Ids, rarities, ability types and numbers belong to `specs/04` section 8. Role tags map onto `ability_type` (`specs/04` section 8.1). The full roster of 50 is issue #289. The three below are the art pilot (issue #291), from the artist's prototypes in `art/reference/`.

| Row | Rarity | Ability | Name | Role | Element | Reference |
|---|---|---|---|---|---|---|
| `comp_l_01` | Legendary | `DAMAGE` | Lucy | Arcane / Caster | LIGHT (arcane) | `art/reference/lucy.jpeg`, alt `lucy_special.jpeg` |
| `comp_l_04` | Legendary | `BUFF_ATK` | Valeria | Tank / Paladin | LIGHT | `art/reference/valeria.jpeg` |
| `comp_m_01` | Mythic | `DAMAGE` | Seraphine | Warrior / Death Knight | DARK | `art/reference/seraphine.jpeg` |

## Lucy
- **Hook:** a stargazing witch who reads the sky like a map and redraws it when it disagrees with her.
- **Silhouette:** a wide-brim witch hat with a white lily, long wavy blonde hair with a braid, and an astrolabe staff taller than she is.
- **Palette:** violet, gold and cream; amethyst accents.
- **Ability VFX (`vfx_comp_l_01`):** a gold ring of crescent moons opens over the enemy and a starfall drops through it.
- **Variants:** `lucy.jpeg` is the in-game look; `lucy_special.jpeg` is a possible mature skin (`specs/00` decision 105). Store art uses the covered look only.

## Valeria
- **Hook:** a paladin captain who leads from the front; the banner she plants turns a skirmish into a charge.
- **Silhouette:** a winged circlet, long braided blonde hair, a gold-trimmed white plate, a big cross shield and a lance-banner.
- **Palette:** white, gold and royal blue; fleur-de-lis motif.
- **Ability VFX (`vfx_comp_l_04`):** a blue-and-gold banner unfurls behind the squire; a golden aura stays on the hero while the buff lasts.

## Seraphine
- **Hook:** a death knight who took a reaper's scythe as payment and never gave it back.
- **Silhouette:** curling black horns with a red rose, long silver hair, a skull pauldron, a tattered crimson cape and a huge skull scythe.
- **Palette:** black, crimson and magenta, with gold trim.
- **Ability VFX (`vfx_comp_m_01`):** a crimson crescent slash crosses the screen and leaves fading magenta wisps.

## Pilot asset status (issue #291)
The images in Unity are **stand-ins cut from the reference sheets** by `tools/art/cut_pilot_assets.py`. They don't replace the artist's deliverables:

| Asset | Stand-in | Final, from the artist |
|---|---|---|
| Portrait `portrait_comp_*` (256 rp) | crop of the splash art | clean portrait |
| Icon `icon_comp_*` (128 rp) | face crop | icon drawn for 88-128 rp |
| Splash `art_splash_*` (1080 x 1400 rp) | splash without the baked-in text | clean illustration |
| Battle chibi `spr_comp_*` (about 48 px native) | the reference chibi sampled on its own pixel grid, background and pedestal removed, 1 frame | idle 4 + cast 6 pixel frames |
| VFX `vfx_comp_*` | none (the generic ring placeholder) | particle system |
