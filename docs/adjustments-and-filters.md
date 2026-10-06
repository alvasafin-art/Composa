# Adjustments and filters

An **adjustment** changes colors and tones. Applied from Image it changes the active layer's pixels; Layer > New Adjustment Layer creates an editable layer affecting those below it, with the selection as its mask. Ordinary **Filter** commands change target pixels within the selection. **Filter > Add Smart Filter** instead keeps the original source and an editable filter stack, including on smart objects; see [non-destructive editing](professional-editing.md).

Every dialog previews on the canvas as you drag, with a Preview checkbox to compare. Sliders are dragged; hold Alt for finer steps, double-click to type a value, and a Reset button then puts the slider back to the value that changes nothing, or to a filter's default.

## Adjustments

- **Curves** (Ctrl+M): a curve over a histogram, for all channels or for red, green or blue. Click to add a point, drag it, and drag it off the graph to remove it; up to sixteen points.
- **Levels** (Ctrl+L): input black and white points, a midtone gamma and output black and white, per channel, over a histogram, with an Auto button. Image > Auto Levels (Ctrl+Shift+L) applies the automatic stretch directly.
- **Hue/Saturation** (Ctrl+U): hue, saturation and lightness for all colors or for one range (reds, yellows, greens, cyans, blues, magentas). Colorize tints the whole layer with one hue.
- **Brightness/Contrast**, each from -100 to 100.
- **Exposure**: exposure in stops, an offset for the shadows and a gamma.
- **Black & White**: how light each color range comes out, from -200 to 300, and an optional tint with a hue and a saturation for a sepia or a cyanotype.
- **Color Balance**: cyan to red, magenta to green and yellow to blue for the shadows, midtones and highlights separately, with Preserve Luminosity keeping each pixel's brightness.
- **Gradient Map**: maps the tones onto a gradient from a shadows color to a highlights color, with buttons for the current foreground and background colors and a Reverse option.
- **Grain**: film grain with an amount, a size and a roughness.
- **Invert** (Ctrl+I): inverts the colors.
- **Gaussian Blur**, **Motion Blur** and **Add Noise** exist as adjustment layers, so a blur or a grain can sit above a stack and be turned off later; as direct edits they are in the Filter menu.

The Hue/Saturation, Black & White and Color Balance sliders show their colors on the track, so you see what a slider does before you drag it.

## Filters

- **Gaussian Blur**: a radius in pixels. A layer that fills the canvas keeps its edge colors; a floating layer's blur spreads past its edges and the layer grows to hold it.
- **Motion Blur**: a distance and an angle, with a dial that turns the full circle.
- **Add Noise**: an amount, an even or a bell-shaped distribution, gray or colored.
- **Sharpen**: an amount and a radius.
- **Vignette**: blends a color into the edges while keeping the center. Amount, Midpoint (where the falloff starts), Roundness (from following the frame to a circle), Feather and Highlights, which spares bright pixels near the edge. On an empty layer it paints across the whole canvas, so a vignette can live on its own layer above a photo.
- **Bloom / Glow**: makes the bright parts glow, with an amount and a radius.
- **Tonal Contrast**: local contrast, with an amount, a radius and how much the shadows, midtones and highlights each get.
- **Lens Correction**: removes barrel distortion (positive) or pincushion distortion (negative).
- **Remove Background**: makes the plain backdrop connected to the layer's edges transparent. Tolerance says how different a pixel may be from the backdrop and still go. It suits product shots and portraits on a plain background; it is not a subject detector.
- **Camera Raw Filter**: a full grading panel, described in [Camera Raw Filter](camera-raw.md).
- **Painterly**: described below.

## Painterly

Painterly repaints a layer in brush strokes that follow the picture, so a photo becomes a painting that is still recognizably the same photo. The largest brush paints first. Each smaller brush then repaints only the places where the canvas still differs from the picture, so flat areas stay loose while eyes, mouths and edges are painted finely. Every stroke takes its color from the picture and runs along the edge it started on.

- **Style**: Impressionist paints faithfully; Expressionist uses long strokes that bend with the picture and lets colors drift; Colorist Wash lays thin, overlapping washes; Pointillist paints dots.
- **Brush Size**: the diameter of the largest brush, from 0 to 200 pixels. At 0 the brush fits itself to the picture, a fiftieth of its shorter side.
- **Passes**: how many brushes are used, from 1 to 4, each half the size of the last.
- **Detail**: from 0 to 100, how closely the strokes follow the picture. Higher paints more of it again with the smaller brushes.

Gaps between strokes stay transparent, so a layer filled with a paper color below the painting gives a painting on paper. On a large photo the Expressionist style's smallest brush can get very thin and the result looks hairy; a larger brush size or fewer passes fixes that. The strokes are laid down within a few seconds on a photo of a few megapixels.
