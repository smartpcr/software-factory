# Forge — guide

Operator-facing material for Forge, captured from a running instance on
the real **code-intelligence** product and organised around six scenarios:

1. Operator setup: product-owner role, provider connections, crews, preferred models
2. Product setup: repo, sync, build + test gate
3. Work items: sync and live execution
4. Stories: spawn, plan docs, implementation ⇄ review, release, workstreams
5. Guardrails against hallucination and sloppiness; system and user prompt context
6. Reports: cost and performance

| Item | What it is |
|------|------------|
| [usage-guide.md](usage-guide.md) | The scenario-by-scenario guide with screenshots and animations |
| [forge-guide-slides.pptx](forge-guide-slides.pptx) | Slide deck version of the guide |
| [media/forge-intro.mp4](media/forge-intro.mp4) | Two-minute captioned intro: spawn a story, spec (plan docs), implementation ⇄ review, release |
| [media/forge-walkthrough.mp4](media/forge-walkthrough.mp4) | Five-minute captioned walkthrough through all six scenarios |
| [images/](images/) | Full-page screenshots used by the guide and the deck |
| [media/](media/) | GIF clips and the video |
| [capture/capture.mjs](capture/capture.mjs) | Playwright script that regenerates every screenshot, GIF and the video |
| [capture/build-slides.cjs](capture/build-slides.cjs) | Rebuilds the slide deck from the screenshots (needs `pptxgenjs`) |

The operator identity is redacted in-page during capture; everything else
is live data. See section 8 of the usage guide for how to regenerate.
