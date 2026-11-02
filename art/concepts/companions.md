# Companion concepts

Presentation only: names, roles and look briefs for the rows in `content_src/companions.csv`. Ids, rarities, ability types and numbers belong to `specs/04` section 8. Role tags map onto `ability_type` (`specs/04` section 8.1). The full roster of 50 is issue #289. The three below are the art pilot (issue #291), from the artist's prototypes in `art/reference/companions/`.

| Row | Rarity | Ability | Name | Role | Element | Reference |
|---|---|---|---|---|---|---|
| `comp_l_01` | Legendary | `DAMAGE` | Lucy | Arcane / Caster | LIGHT (arcane) | `art/reference/companions/lucy.jpeg`, alt `lucy_special.jpeg` |
| `comp_l_04` | Legendary | `BUFF_ATK` | Valeria | Tank / Paladin | LIGHT | `art/reference/companions/valeria.jpeg` |
| `comp_m_01` | Mythic | `DAMAGE` | Seraphine | Warrior / Death Knight | DARK | `art/reference/companions/seraphine.jpeg` |
| `comp_l_02` | Legendary | `DAMAGE` | Yuki | Mage / Shadow | DARK | `art/reference/companions/yuki.jpg`, splash `splash/yuki_splash.png` |

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

## Yuki
- **Hook:** a gothic shadow mage who never lowers her parasol; when she twirls it, the shade breaks into butterflies.
- **Silhouette:** long black hair with a white streak, a heart-and-cross bow, a halo of thorns, a frilled black-and-white gothic dress and a frilled black parasol.
- **Palette:** black, white and violet; silver chains and cross accents.
- **Ability VFX (`vfx_comp_l_02`):** violet shadow butterflies swarm out of the parasol and burst on the enemy.
- **Added after the pilot**, the same way: stand-ins cut by `tools/art/cut_pilot_assets.py`.
- **Splash pilot:** the first dedicated vertical splash (`splash/yuki_splash.png`, 1024 x 1536). The splash stand-in is cut from it, not from the sheet; icon, portrait and chibi still come from the sheet, whose face crop has more pixels.

## Pilot asset status (issue #291)
The images in Unity are **stand-ins cut from the reference sheets** by `tools/art/cut_pilot_assets.py`. They don't replace the artist's deliverables:

| Asset | Stand-in | Final, from the artist |
|---|---|---|
| Portrait `portrait_comp_*` (256 rp) | crop of the splash art | clean portrait |
| Icon `icon_comp_*` (128 rp) | face crop | icon drawn for 88-128 rp |
| Splash `art_splash_*` (1080 x 1400 rp) | splash without the baked-in text (Yuki: from her vertical splash) | clean illustration |
| Battle chibi `spr_comp_*` (about 48 px native) | the reference chibi sampled on its own pixel grid, background and pedestal removed, 1 frame | idle 4 + cast 6 pixel frames |
| VFX `vfx_comp_*` | none (the generic ring placeholder) | particle system |

### Splash pilot notes (Yuki)
- **What arrived:** a 2:3 vertical splash (1024 x 1536) with the title, class and element chips baked into the top 265 px.
- **What the game needs:** 1080 x 1400 rp (about 0.77:1) with no baked-in text (`specs/07` section 15.2). The UI draws name, rarity and role.
- **Stand-in cut:** everything below the chips, x 24-1001 and y 270-1536, scaled up about 1.1x. It keeps the full figure from halo to boots, and loses about 2% of the parasol on the right and the hair ends on the left.
- **Ask the artist for:** the same composition with no text, at 1080 x 1400 or larger, keeping the head and the halo inside the top 20%. Then nothing is lost to the crop and nothing is upscaled.

